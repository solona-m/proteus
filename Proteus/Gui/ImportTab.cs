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
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

internal sealed class ImportTab
{
    private readonly ContentImportService contentImport;
    private readonly LuminisImportService luminisImport;
    private readonly StatusWindow window;
    private readonly EmissiveSkinImportService emissiveImport;
    private readonly EyeImportService eyeImport;
    private readonly OnionImportService onionImport;
    private readonly FileDialogManager fileDialog;
    private readonly Configuration config;
    private readonly SidecarDiscoveryService discovery;
    private readonly PresetService presets;

    public ImportTab(ContentImportService contentImport, LuminisImportService luminisImport, StatusWindow window, EmissiveSkinImportService emissiveImport, EyeImportService eyeImport, OnionImportService onionImport, FileDialogManager fileDialog, Configuration config, SidecarDiscoveryService discovery, PresetService presets)
    {
        this.contentImport = contentImport;
        this.luminisImport = luminisImport;
        this.window = window;
        this.emissiveImport = emissiveImport;
        this.eyeImport = eyeImport;
        this.onionImport = onionImport;
        this.fileDialog = fileDialog;
        this.config = config;
        this.discovery = discovery;
        this.presets = presets;
    }

    /// <summary>Amber for "this worked, but read it" — the Import tab's pack warnings and its result line.</summary>
    // A property: ProteusStyle.Warn follows the active Dalamud style, which a static field would freeze.

    // ── Import tab state ──
    // The parsed pack, held across frames from Browse until Import. Null = nothing picked yet.
    private OnionImportService.ImportPreview? _importPreview;
    // Material paths the pack's layouts target, resolved once per pick: too slow for the draw.
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _importMaterials;
    // Whether that list came from game data rather than the female-only fallback, which must be said.
    private bool _importMaterialsFromGameData;
    private string _importPath = "";      // what the dialog returned, kept so a parse failure can name it
    private string _importName = "";      // editable mod name, pre-filled from the pack
    private string _importAuthor = "";
    private bool _importAsTex;            // convert layers to BC7 .tex instead of keeping the pack's PNGs
    private string? _importStatus;
    private bool _importStatusOk;
    // The import worked but the user must still act: amber, not green.
    private bool _importStatusWarn;
    private bool _importBusy;             // an import is running on the pool — the button is inert meanwhile
    // Written by the pool task, read and cleared on the framework thread; a reference assignment is the whole handoff.
    private volatile OnionImportService.PreparedImport? _importPrepared;

    // ── Content (.pmp) import state ──
    // Same three-phase handoff as the Onion import, in separate fields so one pack kind can't import under another's rules.
    private ContentImportService.ImportPreview? _contentPreview;
    private volatile ContentImportService.PreparedImport? _contentPrepared;
    // A registration still writing a v3 pack's piece group on the pool; pumped every frame until it answers.
    private ContentImportService.PreparedImport? _contentAwaited;

    // ── Atramentum Luminis (.ttmp2) import state ──
    // Kept apart from the other imports' fields for the same reason.
    private LuminisImportService.ImportPreview? _luminisPreview;
    private volatile LuminisImportService.PreparedImport? _luminisPrepared;
    // Body material suffix the overlays target: seeded on pick, then owned by the user's combo.
    private string _luminisSuffix = "";
    // Resolved once per pick, like _importMaterials.
    private IReadOnlyList<string>? _luminisMaterials;
    private bool _luminisMaterialsFromGameData;
    // A registration Penumbra accepted but hasn't finished loading; pumped every frame (see TickLuminisImport).
    private bool _luminisAwaiting;
    private LuminisImportService.PreparedImport? _luminisAwaited;

    // ── Preset (.ptp) import state ──
    // A preset installs no mod: it asks which installed mod to add to, and never auto-imports.
    private sealed class PresetImport
    {
        public required ModPreset Preset;

        /// <summary>Every Proteus mod it could be added to, for the picker.</summary>
        public required List<OverlayEntry> Candidates;

        /// <summary>The chosen target, empty when the preset's own mod name matched none of them.</summary>
        public string ModDirectory = string.Empty;

        /// <summary>Whether <see cref="ModDirectory"/> was matched from the preset's saved mod name.</summary>
        public bool Matched;
    }

    private PresetImport? _presetImport;

    // ── Eye pack (.zip) import state ──
    // A fourth set, kept apart from the other three for the reason given above.
    private EyeImportService.ImportPreview? _eyePreview;
    private volatile EyeImportService.PreparedImport? _eyePrepared;
    // A registration Penumbra has accepted but not finished loading; see TickEyeImport.
    private bool _eyeAwaiting;
    private EyeImportService.PreparedImport? _eyeAwaited;

    // ── Emissive skin (.pmp) import state ──
    // Shares .pmp with the content import: LoadPenumbraPack picks the reader, and separate previews keep a stale panel from overriding it.
    private EmissiveSkinImportService.ImportPreview? _emissivePreview;
    private volatile EmissiveSkinImportService.PreparedImport? _emissivePrepared;
    // Body material suffix, owned by the user's combo once seeded, like _luminisSuffix.
    private string _emissiveSuffix = "";
    private IReadOnlyList<string>? _emissiveMaterials;
    private bool _emissiveMaterialsFromGameData;
    // A registration Penumbra has accepted but not finished loading; see TickEmissiveImport.
    private bool _emissiveAwaiting;
    private EmissiveSkinImportService.PreparedImport? _emissiveAwaited;
    // A pick whose textures are being read on the pool: the only loader that doesn't answer on the same frame.
    private bool _emissiveLoading;
    // Which pick the running read belongs to, bumped for every pick; a stale token's result is dropped. Framework thread only.
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

    // ── Import tab ───────────────────────────────────────────────────────────

    /// <summary>
    /// Finish an import whose disk work completed on the pool: register the mod with Penumbra and report the outcome.
    /// Driven from <see cref="Plugin.DrawUi"/> so it completes with the window closed; Penumbra IPC keeps it on the framework thread.
    /// </summary>
    /// <param name="unloading">Called from <see cref="Plugin.Dispose"/>: register the mod so it isn't orphaned, and nothing else.</param>
    public void TickImport(bool unloading = false)
    {
        TickContentImport(unloading);
        TickLuminisImport(unloading);
        // Skipped during teardown: nothing could draw the preview or register an auto-import.
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

        // Clear the pack on success so it can't be re-imported; guarded on identity since the user may have picked another meanwhile.
        if (r.Ok && ReferenceEquals(_importPreview, done.Preview))
        {
            _importPreview = null;
            _importMaterials = null;
        }

        // Show the result even if the user closed the window.
        if (!window.IsOpen) window.Show(forceExpand: true);
    }

    /// <summary>
    /// The content-pack half of <see cref="TickImport"/>, on the same terms: the disk work runs on the
    /// pool, and the Penumbra registration has to land back here on the framework thread.
    /// </summary>
    private void TickContentImport(bool unloading)
    {
        if (_contentAwaited is { } awaited)
        {
            // Teardown: no frames left to pump into. Finished quietly if the write already landed, else left.
            if (unloading) { contentImport.FinishPendingOnUnload(); return; }

            if (contentImport.Pump() is not { } pumped) return;   // still writing — next frame
            _contentAwaited = null;
            FinishContentImport(pumped, awaited);
            return;
        }

        var done = _contentPrepared;
        if (done == null) return;
        _contentPrepared = null;

        var r = contentImport.Register(done, quiet: unloading);
        if (unloading) return;

        if (r == null)
        {
            // The piece group is being written on the pool. Hold the busy flag and keep pumping.
            _contentAwaited = done;
            return;
        }

        FinishContentImport(r.Value, done);
    }

    private void FinishContentImport(
        ContentImportService.ImportResult r, ContentImportService.PreparedImport done)
    {
        _importBusy = false;
        _importStatus = r.Message;
        _importStatusOk = r.Ok;
        _importStatusWarn = r.Warning;

        if (r.Ok && ReferenceEquals(_contentPreview, done.Preview))
            _contentPreview = null;

        if (!window.IsOpen) window.Show(forceExpand: true);
    }

    /// <summary>The Atramentum Luminis half of <see cref="TickImport"/>; spans frames, since Penumbra won't enable a mod it is still loading.</summary>
    private void TickLuminisImport(bool unloading)
    {
        if (_luminisAwaiting)
        {
            // Nothing to pump into during teardown: there are no more frames, no one to read a status
            // Nothing to pump into during teardown; the mod is already added, so it isn't orphaned.
            if (unloading) return;

            // Still loading: try again next frame.
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

        if (!window.IsOpen) window.Show(forceExpand: true);
    }

    /// <summary>The emissive-skin half of <see cref="TickImport"/>, pumped until Penumbra finishes loading the mod.</summary>
    private void TickEmissiveImport(bool unloading)
    {
        if (_emissiveAwaiting)
        {
            // Nothing to pump into during teardown.
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

        if (!window.IsOpen) window.Show(forceExpand: true);
    }

    /// <summary>The eye-pack half of <see cref="TickImport"/>, pumped until Penumbra finishes loading the mod.</summary>
    private void TickEyeImport(bool unloading)
    {
        if (_eyeAwaiting)
        {
            // Nothing to pump into during teardown.
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

        if (!window.IsOpen) window.Show(forceExpand: true);
    }

    internal void DrawImportTab()
    {
        var ims = Strings.Import;
        var cms = Strings.Content;

        // Indent is only a left inset; ImGui hangs wrapped lines itself. Not cached: Strings.Import is replaced on a language change.
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

        // One button for every format; the reader is chosen by extension. The label is stripped of the filter syntax's characters
        // so a translation can't corrupt the filter.
        if (ImGui.Button(ims.BrowseBtn))
            fileDialog.OpenFileDialog(ims.DialogTitle,
                FilterLabel(ims.DialogFilter)
                    + "{" + PenumbraPackage.Extension
                    + "," + OnionPackage.Extension
                    + "," + TexToolsPackage.Extension
                    + "," + EyePackage.Extension
                    + "," + PresetCodec.FileExtension + "}",
                (ok, paths) =>
                {
                    if (!ok) return;
                    var picked = paths.FirstOrDefault();
                    if (string.IsNullOrEmpty(picked)) return;
                    RememberImportDir(picked);
                    LoadPack(picked);
                    AutoImport();
                }, 1, LastImportDir());
        // SameLine inside each branch: with nothing picked there is no name beside the button. Presets first: they install nothing.
        if (_presetImport != null)
        {
            ImGui.SameLine();
            DrawPresetImport(_presetImport);
            return;
        }

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

        // The one loader that answers on a later frame (see LoadEmissivePack).
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
            ImGui.TextColored(ProteusStyle.Warn, w);
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();

        bool valid = preview.AnyImportable && !string.IsNullOrWhiteSpace(_importName) && !_importBusy;
        using (ImRaii.Disabled(!valid))
            // Both captions carry the same "###importGo" id, so the button stays one widget across the busy flip.
            if (ImGui.Button(_importBusy ? ims.ImportBusy : ims.ImportBtn))
                StartImport(preview);
        if (!valid && !_importBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(preview.AnyImportable ? ims.NeedName : ims.NothingUsable);

        DrawImportStatus();
    }

    /// <summary>Read a picked pack with the reader its extension calls for; anything else goes to the Penumbra reader, which rejects it with a true message.</summary>
    private void LoadPack(string path)
    {
        // Every pick supersedes an emissive read still running; done here, the one place a pick arrives, so no loader can forget.
        _emissivePickToken++;
        _emissiveLoading = false;

        // Cleared here for the same reason.
        _presetImport = null;

        if (path.EndsWith(PresetCodec.FileExtension, StringComparison.OrdinalIgnoreCase)) LoadPresetFile(path);
        else if (path.EndsWith(OnionPackage.Extension, StringComparison.OrdinalIgnoreCase)) LoadOnionPack(path);
        else if (path.EndsWith(TexToolsPackage.Extension, StringComparison.OrdinalIgnoreCase)) LoadLuminisPack(path);
        else if (path.EndsWith(EyePackage.Extension, StringComparison.OrdinalIgnoreCase)) LoadEyePack(path);
        else LoadPenumbraPack(path);
    }

    /// <summary>
    /// Choose between the two <c>.pmp</c> readers from the manifest alone (<see cref="EmissiveSkinImportService.Claims"/>), passing the
    /// parsed manifest on. A read that throws falls through to the content importer, which reports it.
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
    /// Import a just-picked pack outright when the preview has nothing to warn about. Each clause is one of the amber warnings
    /// the panel draws; informational lines are not clauses. The preview stays on screen afterwards.
    /// </summary>
    private void AutoImport()
    {
        // A preset is never auto-imported: it needs a target mod.
        if (_presetImport != null) return;

        if (_importBusy || string.IsNullOrWhiteSpace(_importName)) return;

        if (_contentPreview is { } content)
        {
            // No piece that came out wrong (FaultyUnits): the same test the result colour uses.
            if (content.CanImport && content.Warnings.Count == 0 && content.FaultyUnits == 0)
                StartContentImport(content);
            return;
        }

        if (_eyePreview is { } eye)
        {
            // Every file recognised, a glow to add and no warnings; "no animation" is a decision, so it earns the second click.
            if (eye.AnyImportable && eye.Warnings.Count == 0 && eye.CanGlow
             && eye.Files.All(f => f.Import) && eyeImport.IrisesFromGameData)
                StartEyeImport(eye);
            return;
        }

        if (_luminisPreview is { } luminis)
        {
            // Every texture in and no warnings; each warning here is a decision.
            if (luminis.AnyImportable
             && luminis.Warnings.Count == 0
             && luminis.Textures.All(t => t.Import)
             && (_luminisMaterials is null or { Count: 0 } || _luminisMaterialsFromGameData))
                StartLuminisImport(luminis);
            return;
        }

        if (_emissivePreview is { } emissive)
        {
            // Same test as the Atramentum Luminis arm.
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
         // Only a warning when there IS a material list to be wrong about; an unresolved one prints nothing.
         && (_importMaterials is null or { Count: 0 } || _importMaterialsFromGameData))
            StartImport(onion);
    }

    /// <summary>The folder a pack was last picked from, or null if it no longer exists.</summary>
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

    /// <summary>The default name for an imported pack: the pack's own plus " (Proteus)", since the import sits beside the original.
    /// Left alone if it already says Proteus.</summary>
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
        // A stale preview of another pack kind would keep drawing its own panel over this one.
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
            // Best effort: a failure only costs the preview's material list.
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

    /// <summary>Hand the disk work to the pool; <see cref="TickImport"/> picks the result up and registers it.</summary>
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

    /// <summary>A picked <c>.ptp</c>, matched to an installed mod by name, since directories differ between machines.</summary>
    private void LoadPresetFile(string path)
    {
        _importPath = path;
        _importStatus = null;
        _importPreview = null;    // see LoadOnionPack
        _contentPreview = null;
        _luminisPreview = null;
        _emissivePreview = null;
        _eyePreview = null;
        _importMaterials = null;
        // Blanked so a leftover name can't make AutoImport create a mod from this.
        _importName = string.Empty;

        var result = PresetCodec.FromFile(path);
        if (result.Preset is not { } preset)
        {
            _importStatus = string.Format(Strings.Presets.ImportFailedFmt, result.Error);
            _importStatusOk = false;
            return;
        }

        var mods    = discovery.DiscoverAll();
        var matches = mods
            .Where(m => string.Equals(m.ModName, preset.ModName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _presetImport = new PresetImport
        {
            Preset     = preset,
            Candidates = mods,
            // Only an unambiguous match preselects.
            ModDirectory = matches.Count == 1 ? matches[0].ModDirectory : string.Empty,
            Matched      = matches.Count == 1,
        };
    }

    /// <summary>The preset preview; adds it to the mod's saved presets without wearing it.</summary>
    private void DrawPresetImport(PresetImport state)
    {
        var ps = Strings.Presets;
        var p  = state.Preset;

        ImGui.TextUnformatted(Path.GetFileName(_importPath ?? string.Empty));

        using (ProteusStyle.Card(state.Matched ? null : ProteusStyle.Warn))
        {
            ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - ProteusStyle.S(14f));
            ImGui.TextUnformatted(string.Format(ps.ImportedFromFmt,
                p.Name, p.ModName ?? "?", p.ModAuthor ?? "?"));
            if (!state.Matched)
                ImGui.TextColored(ProteusStyle.Warn, ps.NoMatchingMod);
            ImGui.PopTextWrapPos();
        }

        var target = state.Candidates
            .FirstOrDefault(m => string.Equals(m.ModDirectory, state.ModDirectory, StringComparison.OrdinalIgnoreCase));

        ImGui.TextUnformatted(ps.AddTo);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ProteusStyle.S(260f));
        if (ImGui.BeginCombo("##presetImportMod", target?.ModName ?? ps.PickAMod))
        {
            for (var i = 0; i < state.Candidates.Count; i++)
            {
                var m = state.Candidates[i];
                using var _ = ImRaii.PushId(i);
                if (ImGui.Selectable(m.ModName, m.ModDirectory == state.ModDirectory))
                    state.ModDirectory = m.ModDirectory;
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(target == null))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, ps.AddStaged) && target != null)
            {
                var added = presets.Add(target.ModDirectory, p);
                _presetImport = null;
                _importStatus = string.Format(ps.AddedToFmt, added.Name, target.ModName);
                _importStatusOk = true;
            }
        ProteusStyle.ReasonTooltip(target == null ? ps.PickAMod : ps.AddToTip);
    }

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
            // No "(Proteus)" on a pack that already is one.
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

        // A ready-made Proteus mod is installed unchanged; said plainly, not as a warning. See ImportPreview.InstallOnly.
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

        // One row per piece, with race variants folded into the third column.
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

                    // The vanilla item the pack replaces, falling back to the set id.
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
                    // Amber only for a piece that came out wrong; a body-only model is dropped on purpose.
                    else if (lead.BodyOnly)
                        ImGui.TextDisabled(cms.BodyOnly);
                    else
                        ImGui.TextColored(ProteusStyle.Warn, cms.Unbound);

                    if (lead.Problem != null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(cms.ProblemFmt, unit.Label, lead.Problem));
                }
        }

        // Said before the button: pieces arriving switched off would otherwise look like a failure.
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
            ImGui.TextColored(ProteusStyle.Warn, w);
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

    /// <summary>Parse a picked <c>.ttmp2</c> into the Atramentum Luminis preview, or report why not. Decodes every candidate
    /// texture: whether it is an AL pack is a question about its pixels.</summary>
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
            // Best effort: a failure only costs the preview's material list.
            try
            {
                _luminisMaterials = luminisImport.MaterialsFor(preview, StatusWindow.NullIfEmpty(_luminisSuffix));
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

        // One row per picture, not per manifest path: AL packs alias one texture to a path per race.
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

                    // The glow percentage is the evidence this is an AL pack, so it goes in the table.
                    ImGui.TableNextColumn();
                    if (t.Import) ImGui.TextUnformatted(string.Format(ls.GlowFmt, t.GlowFraction));
                    else ImGui.TextColored(ProteusStyle.Warn, ls.Skipped);

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
            // A body the combo doesn't list is still the live choice and must stay selectable.
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

        // Said before the button, in plain text: both notes are true of a correct import.
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
            ImGui.TextColored(ProteusStyle.Warn, w);
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

        // Outside the collapsing header: a fallback list names no male body, and nobody expands it to check.
        if (!_luminisMaterialsFromGameData)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, ims.FallbackBodies);
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
    /// Start reading a picked <c>.pmp</c> into the emissive-skin preview on the pool: full-resolution masks take too long to decode
    /// on the framework thread. Penumbra is asked about the wearer here; <see cref="TickEmissiveInspect"/> shows the result.
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

        // Framework thread only: asks Penumbra what the character has on.
        var wearer = emissiveImport.DetectWearerBody();
        var token = _emissivePickToken;

        Task.Run(() =>
        {
            try
            {
                var preview = emissiveImport.Inspect(path, pack, wearer);
                // Resolved beside the preview, off the draw; best effort, a failure only costs the material list.
                IReadOnlyList<string>? materials = null;
                bool fromGameData = false;
                try
                {
                    materials = emissiveImport.MaterialsFor(preview, StatusWindow.NullIfEmpty(preview.DefaultSuffix ?? ""));
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

    /// <summary>Put a finished pool read on screen. Nothing else may assign <c>_emissivePreview</c>: these fields move together.</summary>
    private void TickEmissiveInspect()
    {
        var done = _emissiveInspected;
        if (done == null) return;
        _emissiveInspected = null;

        // A read for a replaced file is dropped; the loading flag belongs to the newer pick, so leave it.
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

        // The dialog callback's AutoImport ran before there was anything to import.
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

        // One row per picture, not per manifest path.
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

                    // The glow percentage is the evidence this is glow art, so it goes in the table.
                    ImGui.TableNextColumn();
                    if (t.Import) ImGui.TextUnformatted(string.Format(es.GlowFmt, t.GlowFraction));
                    else ImGui.TextColored(ProteusStyle.Warn, es.Skipped);

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
            // A body the combo doesn't list is still the live choice and must stay selectable.
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

        // Said before the button, in plain text: both notes are true of a correct import.
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
            ImGui.TextColored(ProteusStyle.Warn, w);
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

        // Outside the collapsing header: a fallback list names no male body, and nobody expands it to check.
        if (!_emissiveMaterialsFromGameData)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, ims.FallbackBodies);
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
            (_importName, _importAuthor, _importAsTex, StatusWindow.NullIfEmpty(_emissiveSuffix));
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

    /// <summary>Parse a picked <c>.zip</c> as a loose eye-texture pack, or report why not. Decodes only the mask, so it is cheap
    /// enough for the picking frame.</summary>
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
                    else ImGui.TextColored(ProteusStyle.Warn, es.Skipped);

                    if (f.SkipReason != null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(es.SkippedReasonFmt, f.Name, f.SkipReason));
                }
        }

        // Cutout is baked into the written art, so it is chosen here; the Glow dial only scales what survived.
        if (preview.Fractions != null)
        {
            ImGui.Spacing();
            ImGui.SetNextItemWidth(220);
            // Inert while a write runs: the choice is already snapshotted.
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

        // What the glow will be, said before the button.
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
            ImGui.TextColored(ProteusStyle.Warn, es.FallbackIrises);
            ImGui.PopTextWrapPos();
        }

        foreach (var w in preview.Warnings)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, w);
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
        // Snapshot Cutout: the combo can change it while the pool thread writes.
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
            (_importName, _importAuthor, _importAsTex, StatusWindow.NullIfEmpty(_luminisSuffix));
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
                ImGui.TextColored(ProteusStyle.Warn, ims.Skipped);

            // Hovering the last column explains the layer; rows are only dimmed, not disabled, so hover reaches skipped ones.
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(l.SkipReason == null
                    ? string.Format(ims.LayerImportedFmt, l.File, l.Slot, l.BodyType)
                    : string.Format(ims.LayerSkippedFmt, l.File, l.SkipReason));
        }
    }

    /// <summary>What happens when the pack has nothing painted for the body the user wears; drawn for every pack, single-layout included.</summary>
    private static void DrawImportBodyFit(OnionImportService.ImportPreview preview)
    {
        if (preview.DefaultLayout == null || preview.DefaultLayoutMatchedBody) return;

        var ims = Strings.Import;

        ImGui.Spacing();
        if (preview.WearerBodyType == null)
        {
            ImGui.TextDisabled(string.Format(ims.NotDrawnFmt, preview.DefaultLayout));
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

        // Outside the collapsing header: a fallback list names no male body, and nobody expands it to check.
        var ims = Strings.Import;

        if (!_importMaterialsFromGameData)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, ims.FallbackBodies);
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

    private void DrawImportStatus()
    {
        if (_importStatus == null) return;
        var colour = !_importStatusOk ? new Vector4(1f, 0.5f, 0.4f, 1f)       // failed
                   : _importStatusWarn ? ProteusStyle.Warn                      // worked, but act on it
                   : new Vector4(0.4f, 0.9f, 0.4f, 1f);                        // clean
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(colour, _importStatus);
        ImGui.PopTextWrapPos();
    }
}
