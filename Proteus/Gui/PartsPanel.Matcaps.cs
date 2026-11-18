using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;

namespace Proteus.Gui;

/// <summary>
/// The Matcaps tool's wiring: the pane's context, and Apply (a new matcap, or a change of the ticked parts' matcaps),
/// Undo and Forget. The heavy half of each runs on a
/// worker (<see cref="MatcapStudio.Prepare"/> and <see cref="MatcapWriter"/> take the write locks themselves); the
/// library's collection setting, the drawn-file check and the redraw run back on the framework thread when it is done.
/// </summary>
public sealed partial class PartsPanel
{
    private readonly Configuration matcapConfig;
    private readonly MatcapPanel matcapPanel = new();
    private MatcapIndex? matcapIndex;
    private Task? matcapScan;
    private Task<MatcapJobResult>? matcapJob;
    private string? matcapError;
    private List<MatcapImage>? matcapImages;
    private string? matcapImagesFor;
    private MatcapRecord? matcapRecord;
    private byte[]? matcapRoomFor;
    private string? matcapFolderMade;
    private int matcapRoom;
    private readonly Dictionary<string, (DateTime At, MatcapSelection.Status Status)> matcapLibStatus = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A library's collection status, read over IPC at most every 10 s per library and after each job (the list draws every frame, and Penumbra logs every call).</summary>
    private MatcapSelection.Status CachedLibraryStatus(IMatcapPenumbra adapter, MatcapLibrary lib)
    {
        var key = lib.Mod + "|" + lib.Group;
        if (matcapLibStatus.TryGetValue(key, out var c) && (DateTime.UtcNow - c.At).TotalSeconds < 10) return c.Status;
        var status = MatcapSelection.Read(adapter, lib);
        matcapLibStatus[key] = (DateTime.UtcNow, status);
        return status;
    }
    private IReadOnlyDictionary<string, MatcapWriter.State>? matcapStates;
    private string? matcapRecordFor;

    /// <summary>The plugin's index of matcap libraries, shared and cached across sessions.</summary>
    internal void UseMatcapIndex(MatcapIndex shared) => matcapIndex = shared;

    /// <summary>Proteus's own matcaps: downloaded the first time the Matcaps pane is shown, listed beside the user's.</summary>
    private IMatcapPack? matcapPack;
    private readonly MatcapPackView matcapPackView = new();

    internal void UseMatcapPack(IMatcapPack pack) => matcapPack = pack;

    /// <summary>A part's tick box is enabled: under Matcaps by the matcap rule, else by the switch rule.</summary>
    private bool CanTick(ModelPart part) =>
        tool == Tool.Matcap ? MatcapPlan.Tickable(part, MatcapTargetKind(), parts?.AttributeNames ?? []) : part.Toggleable;

    /// <summary>What the open model is to the Matcaps tool (gear, hair, ear, face), or null.</summary>
    private MatcapPlan.Kind? MatcapTargetKind() =>
        modelIndex >= 0 && modelIndex < models.Count ? MatcapPlan.TargetOf(models[modelIndex].GamePath, out _)?.Kind : null;

    /// <summary>Whether a tool's button tooltip carries the brush's lock hint: only the painting tools lock parts.</summary>
    private static bool ShowsBrushLockHint(Tool value) =>
        !(value is Tool.Navigate or Tool.Retarget or Tool.Matcap || IsPartTool(value));

    /// <summary>Close vanilla horns or ears when leaving Matcaps: no other tool knows a character part.</summary>
    private void CloseVanillaEar()
    {
        ResetForNewModel();
        vanilla = null;
        models = [];
        modelLabels = [];
        modelIndex = -1;
        parts = null;
    }

    /// <summary>What a matcap job left for the framework thread to finish.</summary>
    private sealed record MatcapJobResult(string Root, MatcapWriter.Outcome Outcome, MatcapChoice? Select, bool ConfirmEnable,
                                          IReadOnlyList<string> SiblingsSkipped, bool Lod0Cut, bool AlphaTested, string? GamePath)
    {
        /// <summary>A Change: no model file was written.</summary>
        public bool IsChange { get; init; }
        /// <summary>An Apply: only then is the drawn file checked (Change and Forget write no model).</summary>
        public bool IsApply { get; init; }
        /// <summary>An Apply: the model files written.</summary>
        public int ModelCount { get; init; }
        /// <summary>A row Apply over cut-out holes inside the row's triangles (they show the matcap).</summary>
        public bool RowHoles { get; init; }
        /// <summary>A row Apply on a glass part: its cut copy was moved to character.shpk, drawing this many triangles.</summary>
        public bool RowGlass { get; init; }
        public int RowGlassTriangles { get; init; }
        /// <summary>An Apply wrote one of Proteus's hair ports, which is kept as it is while it has a matcap.</summary>
        public bool PortKept { get; init; }
    }

    private string? MatcapImageFolder()
    {
        if (penumbra.GetModDirectory() is not { } root) return null;
        var dir = Path.Combine(root, SidecarDiscoveryService.ManagedModDir, SidecarDiscoveryService.MatcapsSubdir);
        if (matcapFolderMade == dir) return dir;   // created once, not every frame
        try { Directory.CreateDirectory(dir); } catch { return null; }
        matcapFolderMade = dir;
        return dir;
    }

    private MatcapStudio.Environment MatcapEnv() =>
        new(path => textureLoader.LoadRawFile(null, path), textureLoader, matcapConfig.EnableCompression,
            Path.Combine(Path.GetTempPath(), "Proteus", "matcap"), RowOnly: matcapRowOnly, RowSub: matcapRowSub,
            // Shapes a row matcap's refusal for a part still on the old shader (switch Gear shaders on, or wear it once).
            GearShadersOn: matcapConfig.PluginEnabled && matcapConfig.AutoUpgradeLegacyShaders);

    /// <summary>The colour-table row pair a matcap is cut to (0: the whole part), and its sub-row.</summary>
    private int matcapRowOnly;
    private SubRowPick matcapRowSub = SubRowPick.Both;

    /// <summary>The Row A / Row B toggles (neither or both: both sub-rows).</summary>
    private bool matcapSubA, matcapSubB;

    /// <summary>The material picked for a row matcap with nothing ticked, when the model has more than one (null: the first).</summary>
    private string? matcapRowMaterial;

    /// <summary>Set the row (0: the whole part) and sub-row — the panel's picker and the harness.</summary>
    internal void SetMatcapRow(int row, SubRowPick sub)
    {
        matcapRowOnly = Math.Clamp(row, 0, 16);
        matcapRowSub = sub;
        // The toggles show what the sub-row is: a harness "both" clears them, as the panel's own both-or-neither does.
        if (RowPickerView.Sub(matcapSubA, matcapSubB) != sub) (matcapSubA, matcapSubB) = (sub == SubRowPick.A, sub == SubRowPick.B);
        PumpMatcapPreview();
    }

    /// <summary>No row picked (and the Row A / Row B toggles off): the matcap goes on the ticked parts, whole.</summary>
    private void ClearMatcapRow()
    {
        if (matcapRowOnly == 0 && !matcapSubA && !matcapSubB) return;
        (matcapSubA, matcapSubB) = (false, false);
        SetMatcapRow(0, SubRowPick.Both);
    }

    /// <summary>
    /// The parts Apply and the click preview cover: the ticked ones; or, with a row picked and nothing ticked, every whole
    /// submesh of the row material that can take a matcap (<see cref="MatcapPlan.RowPool"/>) — the row picks the pixels,
    /// so ticks only narrow it.
    /// </summary>
    private List<ModelPart> MatcapApplyParts()
    {
        if (parts == null) return [];
        var chosen = parts.Parts.Where(p => ticked.Contains(p.Label)).ToList();
        if (chosen.Count == 0 && matcapRowOnly > 0 && MatcapRowMaterial() is { } mat)
            chosen = MatcapPlan.RowPool(parts.Parts, MatcapTargetKind(), parts.AttributeNames, mat);
        return chosen;
    }

    /// <summary>
    /// The material a row matcap works on: the ticked parts' (the first one's; two materials are refused), else the one
    /// picked in the material list, else the first that can take one.
    /// </summary>
    private string? MatcapRowMaterial()
    {
        if (parts == null) return null;
        var ticks = parts.Parts.Where(p => ticked.Contains(p.Label)).ToList();
        if (ticks.Count > 0) return ticks.Select(p => p.Material).FirstOrDefault(m => !string.IsNullOrEmpty(m));
        var all = MatcapPlan.RowMaterials(parts.Parts, MatcapTargetKind(), parts.AttributeNames);
        return all.FirstOrDefault(m => string.Equals(m, matcapRowMaterial, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
    }

    /// <summary>The picker's rows for one material, read off the framework thread, by (mod, model, material).</summary>
    private string? matcapRowInfoKey;
    private Task<MatcapStudio.RowInfo?>? matcapRowInfo;

    /// <summary>Whether the row material has colours set in Proteus the cut won't follow, cached by (mod, material, metadata).</summary>
    private (string Key, bool Warn)? matcapRowFollow;

    /// <summary>
    /// The material list (nothing ticked, several materials), then the Colors window's own row picker for the material —
    /// swatches, only the rows its index map uses, hovering a row lights it. Always shown: picking a row is what puts the
    /// matcap on that row only (no row: the ticked parts, whole); clicking it again unpicks it. Row A / Row B narrow it.
    /// </summary>
    private void DrawMatcapRowPicker()
    {
        var s = Strings.Matcap;
        // Wherever the rows can't be shown (no model, the game's own gear, a material without colour rows), no row is
        // picked: a pick made on another model must not linger where nothing can show or clear it.
        if (parts == null) { ClearMatcapRow(); return; }

        // Several materials and nothing ticked: pick which one the row is on.
        bool anyTicked = parts.Parts.Any(p => ticked.Contains(p.Label));
        var materials = MatcapPlan.RowMaterials(parts.Parts, MatcapTargetKind(), parts.AttributeNames);
        var mat = MatcapRowMaterial();
        if (!anyTicked && materials.Count > 1)
        {
            int at = Math.Max(0, materials.FindIndex(m => string.Equals(m, mat, StringComparison.OrdinalIgnoreCase)));
            var labels = materials.Select(MatcapWriter.StemOf).ToArray();
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo($"{s.RowMaterial}##matcapRowMat", ref at, labels, labels.Length))
            {
                matcapRowMaterial = materials[at];
                PumpMatcapPreview();
            }
            mat = materials[at];
        }

        // The rows of the material, read once per (mod, model, material) on a worker.
        var root = ModRoot();
        if (mat == null || root == null || modelIndex < 0 || modelIndex >= models.Count) { ClearMatcapRow(); return; }
        var model = models[modelIndex];
        var key = $"{root}|{model.File}|{mat}";
        if (key != matcapRowInfoKey)
        {
            matcapRowInfoKey = key;
            var env = MatcapEnv();
            matcapRowInfo = Task.Run(() =>
            {
                try { return MatcapStudio.ReadRowInfo(root, model.File, model.GamePath, mat, env); }
                catch (Exception ex) { log.Warning("[Proteus] matcap row picker: {0}", ex.Message); return null; }
            });
        }

        if (matcapRowInfo is not { IsCompleted: true } task) { ImGui.TextDisabled(s.RowReading); return; }
        if (task.Result is not { } info)
        {
            ClearMatcapRow();
            ProteusStyle.DisabledWrapped(s.RowNoRows);
            return;
        }

        int pick = matcapRowOnly;
        bool a = matcapSubA, b = matcapSubB;
        if (ColorTableEditor.DrawRowPicker($"matcaprow_{key}", info.Rows, info.UsedRows, [info.Leaf], ref pick, ref a, ref b, info.UsedSubRow)
            || pick != matcapRowOnly)
        {
            (matcapSubA, matcapSubB) = (a, b);
            SetMatcapRow(pick, RowPickerView.Sub(a, b));
        }
    }

    /// <summary>
    /// Whether the row material has colours set in Proteus for this mod (Colors window rows, keyed by the material's file in
    /// the mod): the cut copy won't follow them. Cached per mod, material and the last composite's entries.
    /// </summary>
    private bool MatcapRowFollowWarn()
    {
        if (matcapRowOnly <= 0 || ModRoot() is not { } root || MatcapRowMaterial() is not { } mat) return false;
        var folder = Path.GetFileName(root.TrimEnd('\\', '/'));
        var meta = compositor.LastDiscovered.FirstOrDefault(e => string.Equals(e.ModDirectory, folder, StringComparison.OrdinalIgnoreCase))?.Metadata;
        // The material files with colour rows, snapshotted here on the framework thread (the Colors window edits that
        // dictionary on this thread) — the worker only reads the snapshot, and its contents key the cache.
        var coloured = (meta?.ContentMaterials ?? [])
            .Where(kv => kv.Value?.ColorTableRows is { Count: > 0 }).Select(kv => kv.Key.Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var key = $"{root}|{mat}|{string.Join("|", coloured)}";
        if (matcapRowFollow is { } c && c.Key == key) return c.Warn;
        if (matcapRowFollowTask is { IsCompleted: false }) return false;   // being read
        if (matcapRowFollowTask is { IsCompletedSuccessfully: true } done && done.Result.Key == key)
        {
            matcapRowFollow = done.Result;
            return done.Result.Warn;
        }
        // The manifest read goes to a worker: the framework thread never waits on it.
        var colouredSet = new HashSet<string>(coloured, StringComparer.OrdinalIgnoreCase);
        matcapRowFollowTask = Task.Run(() =>
        {
            bool warn = colouredSet.Count > 0 && MatcapRowCut.FollowWarn(root, mat, f => colouredSet.Contains(f));
            return (key, warn);
        });
        return false;
    }

    private Task<(string Key, bool Warn)>? matcapRowFollowTask;

    /// <summary>For the harness: whether a mod's material file has colour rows set in Proteus (the last composite's entries).</summary>
    private bool HasColours(string modDir, string materialRel)
    {
        var meta = compositor.LastDiscovered.FirstOrDefault(e => string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase))?.Metadata;
        return meta?.PeekMaterialSettings(materialRel)?.ColorTableRows is { Count: > 0 };
    }

    private void RescanMatcaps(bool force)
    {
        if (matcapScan is { IsCompleted: false }) return;
        if (penumbra.GetAllModsQuiet() is not { } all || penumbra.GetModDirectory() is not { } root) return;
        matcapIndex ??= new MatcapIndex(null);
        var index = matcapIndex;
        matcapScan = Task.Run(() => index.Refresh(all, root, force));
    }

    // ── the click preview ──────────────────────────────────────────────────

    private readonly MatcapPreviewState matcapPreview = new();
    /// <summary>A preview was taken down without a redraw for a job; the job's success redraws, a refusal pays it.</summary>
    private bool matcapRedrawOwed;
    private Task? matcapBuild;
    /// <summary>A built preview waiting to be pushed: the push was refused (busy) or held back (a redraw too soon).</summary>
    private (int Gen, string Key, MatcapPreview.Built Built)? matcapPending;
    private bool matcapPumpQueued;
    private long lastMatcapPushAt = long.MinValue / 2;
    /// <summary>The game paths the shown preview redirects (for the harness's view).</summary>
    private List<string> matcapShownPaths = [];

    /// <summary>A thumbnail was clicked (the panel, or the harness): show it on the character, following the ticks.</summary>
    internal void RequestMatcapPreview(MatcapChoice choice)
    {
        matcapPreview.Click(choice);
        PumpMatcapPreview();
    }

    /// <summary>What the preview would be for now: the key, and why it can't be shown (null when it can).</summary>
    private (string Key, string? Blocked)? MatcapPreviewNow()
    {
        if (matcapPreview.Choice is not { } choice || modelIndex < 0 || modelIndex >= models.Count) return null;
        var chosen = MatcapApplyParts();
        var key = MatcapPreviewState.Key(models[modelIndex].File + (matcapRowOnly > 0 ? $"|row{matcapRowOnly}{matcapRowSub}|{MatcapRowMaterial()}" : ""),
                                         choice, chosen);
        var blocked = MatcapPanelModel.PreviewBlocked(penumbra.IsAvailable, chosen, MatcapTargetKind(), vanilla != null, TargetIsContent,
                                                      choice, matcapRoom, matcapRecord ?? MatcapRecord.Read(ModRoot() ?? ""), matcapRowOnly);
        return (key, blocked);
    }

    /// <summary>
    /// Bring the character's preview in line with the ticks and the choice: build, push, or take it down. Each frame from
    /// the pane, after each build, and on a short retry while something else is busy (so the harness needs no window).
    /// </summary>
    private void PumpMatcapPreview()
    {
        if (!matcapPreview.Wanted && matcapPending == null) return;
        if (tool != Tool.Matcap) { if (matcapPreview.Active) EndLivePreview(refreshGame: true); return; }
        if (MatcapPreviewNow() is not { } now) return;

        if (matcapPending is { } pending)
        {
            if (pending.Gen != matcapPreview.Generation || pending.Key != now.Key || now.Blocked != null) matcapPending = null;
            else { PushMatcapPreview(pending.Gen, pending.Key, pending.Built); return; }
        }

        bool busy = matcapJob is { IsCompleted: false } || preview.Busy || matcapBuild is { IsCompleted: false };
        switch (matcapPreview.Next(now.Key, now.Blocked, busy))
        {
            case MatcapPreviewState.Step.Build:
                StartMatcapPreviewBuild(now.Key);
                break;
            case MatcapPreviewState.Step.End:
                EndLivePreview(refreshGame: true, keepMatcapWanted: true);
                break;
        }
        if (busy) QueueMatcapPump(100);
    }

    /// <summary>One retry of <see cref="PumpMatcapPreview"/> on the framework thread after <paramref name="ms"/>.</summary>
    private void QueueMatcapPump(long ms)
    {
        if (matcapPumpQueued) return;
        matcapPumpQueued = true;
        _ = Plugin.Framework.RunOnTick(() => { matcapPumpQueued = false; PumpMatcapPreview(); }, TimeSpan.FromMilliseconds(Math.Max(1, ms)));
    }

    private void StartMatcapPreviewBuild(string key)
    {
        int gen = matcapPreview.Generation;
        var choice = matcapPreview.Choice!;
        if (ModRoot() is not { } root || modelIndex < 0 || parts == null)
        {
            matcapPreview.Accept(gen, key, "");
            return;
        }
        var model = models[modelIndex];
        var chosen = MatcapApplyParts();
        var drawnAt = OpenModelGamePaths();
        var modsRoot = penumbra.GetModDirectory() ?? "";
        var env = MatcapEnv() with { TempDir = Path.Combine(Path.GetTempPath(), "Proteus", "matcap-preview") };
        // A row matcap previews the cut of the option the character actually wears: where Penumbra resolves the part's
        // material for the player, asked here on the framework thread (the worker only reads the answer).
        string? resolvedMaterial = null;
        if (matcapRowOnly > 0 && MatcapRowMaterial() is { } rowMat && MatcapPlan.TargetOf(model.GamePath, out _) is { } rowTarget
            && MatcapPlan.MaterialGamePaths(rowTarget, rowMat, [], [1]).FirstOrDefault() is { } rowPath)
            resolvedMaterial = penumbra.ResolvePlayer(rowPath);
        matcapBuild = Task.Run(() =>
        {
            MatcapPreview.Built built;
            try
            {
                // The bytes on disk, as Apply reads them.
                var bytes = File.ReadAllBytes(Path.Combine(root, model.File.Replace('\\', Path.DirectorySeparatorChar)));
                built = MatcapPreview.Build(new MatcapPreview.Input(root, model.File, model.GamePath, drawnAt, bytes, chosen, choice, env,
                                                                    modsRoot, resolvedMaterial));
            }
            catch (Exception ex)
            {
                built = MatcapPreview.Built.Refuse(MatcapWriter.CodeFailed, string.Format(Strings.Matcap.PreviewFailedFmt, ex.Message));
            }
            return built;
        }).ContinueWith(t => Plugin.Framework.RunOnFrameworkThread(() => FinishMatcapPreviewBuild(gen, key, t.Result)));
    }

    private void FinishMatcapPreviewBuild(int gen, string key, MatcapPreview.Built built)
    {
        var now = MatcapPreviewNow();
        if (!matcapPreview.Accept(gen, key, now?.Key ?? "") || now?.Blocked != null)
        {
            PumpMatcapPreview();   // something changed while it was built: catch up
            return;
        }
        if (!built.Ok)
        {
            matcapRowGlass = false;   // nothing built: no glass line about a cut that isn't shown
            matcapRowGlassTriangles = 0;
            matcapPreview.Failed(key, built.Message);
            log.Information("[Proteus] matcap preview: not shown ({0}): {1}", built.Code, built.Message);
            // What is on the character no longer matches what is ticked: take it down.
            if (matcapPreview.ShownKey != null) EndLivePreview(refreshGame: true, keepMatcapWanted: true);
            return;
        }
        matcapRowHoles = built.RowHoles;
        matcapRowGlass = built.RowGlass;
        matcapRowGlassTriangles = built.RowGlassTriangles;
        matcapPreviewResolvedFrom = built.ResolvedFrom;
        PushMatcapPreview(gen, key, built);
    }

    /// <summary>Which cut copy the shown row preview pushed: "penumbra" (resolved), "heuristic", or null (not a row preview).</summary>
    private string? matcapPreviewResolvedFrom;

    private void PushMatcapPreview(int gen, string key, MatcapPreview.Built built)
    {
        long nowMs = Environment.TickCount64;
        bool redraw = preview.Unsupported;
        // Without an in-place reload every push redraws the character: at most one per interval.
        if (!MatcapPreviewState.PushDue(nowMs, lastMatcapPushAt, redraw, PreviewIntervalMs))
        {
            matcapPending = (gen, key, built);
            QueueMatcapPump(PreviewIntervalMs - (nowMs - lastMatcapPushAt));
            return;
        }
        // Worked out before our first redirect, which wins every game path while it is on: anything of ours still up
        // (a brush preview) comes down first, without a redraw — this push replaces it.
        string? drawn = null;
        if (matcapPreview.ShownKey == null && ModRoot() is { } root && modelIndex >= 0)
        {
            if (preview.Active) preview.End(redraw: false);
            drawn = DrawnElsewhere(root, models[modelIndex].GamePath, [models[modelIndex].File]);
        }
        if (!preview.PushFiles(built.Files, redraw))
        {
            matcapPending = (gen, key, built);
            QueueMatcapPump(100);
            return;
        }
        matcapPending = null;
        lastMatcapPushAt = nowMs;
        matcapShownPaths = [.. built.Files.SelectMany(f => f.GamePaths).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        matcapPreview.Shown(key, drawn);
        previewDirty = false;
        previewHides = 0;
        share.NotSharing();
        log.Debug("[Proteus] matcap preview: {0} file(s) on the character", built.Files.Count);
    }

    /// <summary>
    /// Before a matcap job: the preview comes off without a redraw (the job's own reload follows), and a redraw is owed
    /// in case the job is refused.
    /// </summary>
    private void TakeDownMatcapPreviewForJob()
    {
        bool shown = matcapPreview.ShownKey != null;
        matcapPending = null;
        EndLivePreview(refreshGame: false);
        matcapRedrawOwed |= shown;
    }

    /// <summary>A job refused (or failed) after the preview came down: redraw so the mod's own files are drawn again.</summary>
    private void PayMatcapRedraw()
    {
        if (!matcapRedrawOwed) return;
        matcapRedrawOwed = false;
        compositor.RedrawForChangedModel("matcap preview");
    }

    private void DrawMatcaps()
    {
        FinishMatcapJob();
        PumpMatcapPreview();
        var root = vanilla == null ? ModRoot() : null;
        if (matcapIndex?.Current == null && matcapScan == null) RescanMatcaps(false);

        // Read the record when the open mod changes or after a write — never every frame, and never a write. Its states
        // (which read files and hash changed row sources) come from a worker: now, and while the pane shows, whenever the
        // worker's manifest check (at most every 2 s) finds the mod's manifest changed.
        var key = root ?? "";
        if (matcapRecordFor != key)
        {
            matcapRecord = root != null ? MatcapRecord.Read(root) : new MatcapRecord();
            matcapRecordFor = key;
            if (root == null) matcapStates = new Dictionary<string, MatcapWriter.State>();
            else StartMatcapDetect(root, force: true);
        }
        if (matcapStatesTask is { IsCompleted: true } done)
        {
            // Cleared on any end, so a failure (a model held open by another program) never stops the checks.
            if (done.IsCompletedSuccessfully)
            {
                if (done.Result.States is { } states) matcapStates = states;
                matcapManifestStamps = done.Result.Stamps;
            }
            else log.Warning("[Proteus] matcap states: the check failed ({0}); it runs again shortly", done.Exception?.GetBaseException().GetType().Name ?? "cancelled");
            matcapStatesTask = null;
        }
        if (root != null && MatcapStatesSchedule.Due(Environment.TickCount64, matcapStatesCheckedAt, matcapStatesTask == null))
            StartMatcapDetect(root, force: false);
        // First use: the pane is shown. Idempotent, so calling it every frame starts one download per session.
        matcapPack?.EnsureAsync();
        var packImages = matcapPackView.Images(matcapPack);

        var folder = MatcapImageFolder();
        var imagesKey = (folder ?? "") + "|" + key;
        if (matcapImages == null || matcapImagesFor != imagesKey)
        {
            matcapImages = MatcapImages.List(folder, root != null ? MatcapImages.ModFolder(root) : null);
            matcapImagesFor = imagesKey;
        }

        var model = modelIndex >= 0 && modelIndex < models.Count ? models[modelIndex] : (PenumbraModMeta.Redirect?)null;
        var chosen = MatcapApplyParts();
        bool rowPool = matcapRowOnly > 0 && parts != null && !parts.Parts.Any(p => ticked.Contains(p.Label));
        // Parsed once per model, not every frame.
        if (!ReferenceEquals(matcapRoomFor, brushBase))
        {
            matcapRoom = 0;
            try { if (brushBase != null) matcapRoom = ModelMaterialWriter.MaterialRoom(brushBase); } catch { }
            matcapRoomFor = brushBase;
        }
        int room = matcapRoom;
        var record = matcapRecord ?? new MatcapRecord();
        var rel = model?.File ?? "";
        var adapter = new PenumbraMatcapAdapter(penumbra);

        DrawMatcapRowPicker();
        matcapPanel.Draw(new MatcapPanel.Context
        {
            Penumbra = penumbra.IsAvailable,
            Busy = matcapJob is { IsCompleted: false },
            Ticked = chosen,
            MaterialRoom = room,
            Images = matcapImages,
            ImageFolder = folder,
            Libraries = matcapIndex?.Current,
            Scanning = matcapScan is { IsCompleted: false },
            ModsRoot = penumbra.GetModDirectory(),
            Record = record,
            States = matcapStates ?? new Dictionary<string, MatcapWriter.State>(),
            Entries = rel.Length > 0 ? [.. record.MaterialsOf(rel)] : [],
            // The files an Undo here restores with this one: the apply group, without the open model.
            GroupFiles = rel.Length > 0 && record.Model(rel) != null
                ? [.. MatcapWriter.ApplyGroupOf(record, rel).Where(f => !PenumbraModMeta.SameFile(f, rel)).Order(StringComparer.OrdinalIgnoreCase)]
                : [],
            LibraryStatus = lib => CachedLibraryStatus(adapter, lib),
            Apply = (choice, confirm) => StartMatcapApply(choice, confirm),
            Undo = () => StartMatcapUndo(forget: false),
            Forget = () => StartMatcapUndo(forget: true),
            Rescan = () => RescanMatcaps(true),
            RefreshImages = () => matcapImages = null,
            Error = matcapError,
            FaceTarget = MatcapTargetKind() == MatcapPlan.Kind.Face,
            HasHorns = parts != null && MatcapPlan.HasHorns(parts.Parts, parts.AttributeNames),
            PackImages = packImages,
            PackStatus = matcapPack?.Status,
            RetryPack = () => matcapPack?.EnsureAsync(retryFailed: true),
            AllParts = parts?.Parts ?? [],
            ModelFile = rel,
            Clicked = RequestMatcapPreview,
            PreviewShown = matcapPreview.ShownKey != null,
            PreviewNote = matcapPreview.Wanted ? matcapPreview.Note : null,
            PreviewDrawnNote = matcapPreview.DrawnNote,
            Row = matcapRowOnly,
            RowPoolMaterial = rowPool ? MatcapRowMaterial() : null,
            GamePiece = vanilla != null,
            RowFollowWarn = MatcapRowFollowWarn(),
            RowHoles = matcapRowHoles,
            RowGlass = matcapRowGlass,
            RowGlassTriangles = matcapRowGlassTriangles,
        });
    }

    /// <summary>The states worker: (states, or null when the manifest was unchanged; the manifest stamps it saw).</summary>
    private Task<(IReadOnlyDictionary<string, MatcapWriter.State>? States, Dictionary<string, long> Stamps)>? matcapStatesTask;
    private long matcapStatesCheckedAt = long.MinValue / 2;
    private Dictionary<string, long>? matcapManifestStamps;
    /// <summary>The last row preview found cut-out holes inside the row's triangles.</summary>
    private bool matcapRowHoles;
    /// <summary>The last row preview or Apply moved a glass part's cut copy to character.shpk, and its triangle count.</summary>
    private bool matcapRowGlass;
    private int matcapRowGlassTriangles;

    /// <summary>
    /// Re-read the entries' states on a worker: <paramref name="force"/> (mod open, after a write) always; otherwise only
    /// when the manifest files changed since the last look. The framework thread only starts the task.
    /// </summary>
    private void StartMatcapDetect(string root, bool force)
    {
        matcapStatesCheckedAt = Environment.TickCount64;
        var before = matcapManifestStamps;
        Func<string, byte[]?> readGame = path => textureLoader.LoadRawFile(null, path);
        matcapStatesTask = Task.Run(() =>
        {
            var stamps = MatcapStatesSchedule.ManifestStamps(root);
            if (!force && !MatcapStatesSchedule.Changed(before, stamps))
                return ((IReadOnlyDictionary<string, MatcapWriter.State>?)null, stamps);
            var started = Environment.TickCount64;
            var states = MatcapWriter.Detect(root, readGame);
            log.Debug("[Proteus] matcap states: {0} entr(ies) in {1} ms", states.Count, Environment.TickCount64 - started);
            return (states, stamps);
        });
    }

    private void StartMatcapApply(MatcapChoice choice, bool confirmEnable)
    {
        matcapError = null;
        if (matcapJob is { IsCompleted: false } || parts == null || modelIndex < 0) return;
        // A pending brush stroke rewrites the whole model from its opening bytes: save it first, or stop.
        if (!FlushPending()) return;   // the save says why in the status line
        var chosen = MatcapApplyParts();
        var env = MatcapEnv();
        // A row matcap is the mod's own gear only: refused here, before any job, for the game's own pieces.
        if (matcapRowOnly > 0 && vanilla != null && MatcapPlan.KindOfApply(chosen) == MatcapPlan.ApplyKind.New)
        {
            matcapError = Strings.Matcap.RowGameGear;
            return;
        }

        // A library that is off is switched on only once the user says so: asked here, before anything is written —
        // whatever the panel's cached status said (EDGE: "Cancel writes nothing").
        if (choice.Library is { } lib && !confirmEnable)
        {
            var st = MatcapSelection.Read(new PenumbraMatcapAdapter(penumbra), lib);
            if (!st.Known) { matcapError = Strings.Matcap.NoPenumbra; return; }
            if (!st.Enabled)
            {
                matcapLibStatus.Clear();
                matcapPanel.InvalidateStatus();   // the panel re-reads it and asks on the next Apply
                matcapError = string.Format(Strings.Matcap.ConfirmEnableFmt, lib.Name, st.Collection, MatcapSelection.AlsoText(lib));
                return;
            }
        }

        // Parts that all have a matcap: those matcaps change in place (the whole material, wherever it is used). A mix of
        // parts with and without one is refused: two writes in one click are not atomic.
        var kind = MatcapPlan.KindOfApply(chosen);
        if (kind == MatcapPlan.ApplyKind.Mixed) { matcapError = Strings.Matcap.AlreadyMatcap; return; }
        if (vanilla == null && ModRoot() == null) return;   // before the preview comes down: no job, no redraw owed
        TakeDownMatcapPreviewForJob();
        if (kind == MatcapPlan.ApplyKind.Change)
        {
            if (ModRoot() is not { } changeRoot) return;
            var names = MatcapPlan.ChangeNames(chosen, MatcapRecord.Read(changeRoot));
            var changePath = models[modelIndex].GamePath;
            matcapJob = Task.Run(() =>
            {
                var outcome = MatcapStudio.ChangeAll(changeRoot, names, choice, env);
                return new MatcapJobResult(changeRoot, outcome, outcome.Ok && choice.IsLibrary ? choice : null, confirmEnable,
                                           [], false, false, changePath) { IsChange = true };
            });
            return;
        }

        if (vanilla is { } piece)
        {
            ApplyMatcapToVanilla(piece, chosen, choice, confirmEnable, env);
            return;
        }
        if (ModRoot() is not { } root) return;
        var model = models[modelIndex];
        var bytes = File.ReadAllBytes(Path.Combine(root, model.File.Replace('\\', Path.DirectorySeparatorChar)));
        matcapJob = Task.Run(() =>
        {
            var prepared = MatcapStudio.Prepare(root, model.File, model.GamePath, bytes, chosen, choice, env);
            var outcome = prepared.Ok ? MatcapWriter.Apply(root, prepared.Request!)
                                      : MatcapWriter.Outcome.Refuse(prepared.Code, prepared.Message);
            // A row matcap over cut-out holes inside the row's triangles: said with the result, since they show the matcap.
            return new MatcapJobResult(root, outcome, outcome.Ok && choice.IsLibrary ? choice : null, confirmEnable,
                                       prepared.SiblingsSkipped, prepared.Lod0Cut, prepared.AlphaTested, model.GamePath)
                { IsApply = true, ModelCount = prepared.Request?.Models.Count ?? 0, RowHoles = prepared.RowHoles,
                  RowGlass = prepared.RowGlass, RowGlassTriangles = prepared.RowGlassTriangles,
                  PortKept = outcome.Ok && MatcapWriter.WritesHairPort(root, prepared.Request) };
        });
    }

    /// <summary>
    /// The game's own gear or horns: the image is baked on a worker first (the slow part), then the copy into its edit mod
    /// runs on the framework thread with a first write that splits the model and places the files — fast work only.
    /// </summary>
    private void ApplyMatcapToVanilla(VanillaPick piece, List<ModelPart> chosen, MatcapChoice choice, bool confirmEnable,
                                      MatcapStudio.Environment env)
    {
        matcapJob = Task.Run(() =>
        {
            // Pre-bake: the first write's own bake then only copies this file.
            var baked = choice;
            if (choice.Image is { } img)
            {
                Directory.CreateDirectory(env.TempDir);
                var temp = Path.Combine(env.TempDir, $"prebake_{Guid.NewGuid():N}.tex");
                var b = MatcapImages.Bake(env.Loader, img.Path, temp, env.Compress);
                if (b.Refusal != null)
                    return new MatcapJobResult("", MatcapWriter.Outcome.Refuse(b.Refusal == "not-square" ? "not-square" : "image-unreadable",
                        string.Format(b.Refusal == "not-square" ? ProteusLoc.Localize("Matcap.Refused.NotSquare.Fmt",
                            "{0} is not square. A matcap is a picture of a sphere filling a square.")
                          : ProteusLoc.Localize("Matcap.Refused.ImageUnreadable.Fmt", "{0} could not be read as an image."), img.Name)),
                        null, false, [], false, false, piece.GamePath);
                baked = MatcapChoice.OfImage(img with { Path = temp });
            }
            return new MatcapJobResult("vanilla", MatcapWriter.Outcome.Done("", []), baked, confirmEnable, [], false, false, piece.GamePath);
        });
        pendingVanillaMatcap = (piece, chosen, choice);
    }

    private (VanillaPick Piece, List<ModelPart> Chosen, MatcapChoice Original)? pendingVanillaMatcap;

    private void StartMatcapUndo(bool forget)
    {
        matcapError = null;
        if (matcapJob is { IsCompleted: false } || ModRoot() is not { } root || modelIndex < 0) return;
        if (!FlushPending()) return;
        TakeDownMatcapPreviewForJob();
        var rel = models[modelIndex].File;
        var undoPath = models[modelIndex].GamePath;
        var undoEnv = MatcapEnv();
        matcapJob = Task.Run(() => new MatcapJobResult(root, forget ? MatcapWriter.Forget(root, rel) : MatcapWriter.Undo(root, rel, undoEnv.ReadGameFile),
                                                       null, false, [], false, false, undoPath));
    }

    /// <summary>On the framework thread, once a job is done: the vanilla copy, the library setting, checks, redraw.</summary>
    private void FinishMatcapJob()
    {
        if (matcapJob is not { IsCompleted: true } job) return;
        matcapJob = null;
        MatcapJobResult r;
        try { r = job.Result; }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] matcap: the job failed");
            matcapError = ex.Message;
            PayMatcapRedraw();
            return;
        }

        if (r.Root == "vanilla" && pendingVanillaMatcap is { } pv)
        {
            pendingVanillaMatcap = null;
            FinishVanillaMatcap(pv.Piece, pv.Chosen, r.Select!, pv.Original, r.ConfirmEnable);
            return;
        }
        Finish(r);
    }

    private void FinishVanillaMatcap(VanillaPick piece, List<ModelPart> chosen, MatcapChoice baked, MatcapChoice original, bool confirm)
    {
        MatcapWriter.Outcome? outcome = null;
        MatcapStudio.Prepared? prepared = null;
        var env = MatcapEnv();
        var copy = CopyVanillaForEdit(piece, (root, rel) =>
        {
            // The copy's bytes are the game's: parts read from them line up with the ones ticked.
            var bytes = File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var mine = ModelPartReader.Read(bytes)?.Parts ?? [];
            var parts2 = chosen.Select(c => mine.FirstOrDefault(p => PartClaims.LineUp(c, p))).OfType<ModelPart>().ToList();
            prepared = MatcapStudio.Prepare(root, rel, piece.GamePath, bytes, parts2, baked, env);
            outcome = prepared.Ok ? MatcapWriter.Apply(root, prepared.Request!) : MatcapWriter.Outcome.Refuse(prepared.Code, prepared.Message);
            return new VanillaEditCopy.FirstWrite(outcome.Ok, outcome.Message);
        }, out bool saved);
        if (copy is not { } c || outcome == null)
        {
            matcapError = outcome?.Message ?? status;
            PayMatcapRedraw();
            return;
        }
        // The record names the image the user chose, not the pre-baked temp file.
        if (outcome.Ok && original.Image is { } img)
        {
            var rec = MatcapRecord.Read(c.Root);
            foreach (var m in rec.Materials.Where(m => m.Source.Image == baked.Image?.Name)) m.Source.Image = img.Name;
            MatcapRecord.Write(c.Root, rec);
        }
        Finish(new MatcapJobResult(c.Root, outcome, outcome.Ok && original.IsLibrary ? original : null, confirm,
                                   prepared?.SiblingsSkipped ?? [], prepared?.Lod0Cut ?? false, prepared?.AlphaTested ?? false, piece.GamePath)
            { IsApply = true, ModelCount = prepared?.Request?.Models.Count ?? 0, RowHoles = prepared?.RowHoles ?? false,
              RowGlass = prepared?.RowGlass ?? false, RowGlassTriangles = prepared?.RowGlassTriangles ?? 0,
              PortKept = outcome.Ok && MatcapWriter.WritesHairPort(c.Root, prepared?.Request) });
    }

    private void Finish(MatcapJobResult r)
    {
        var s = Strings.Matcap;
        matcapRecordFor = null;   // re-read
        matcapLibStatus.Clear();
        if (!r.Outcome.Ok)
        {
            matcapError = r.Outcome.Message;
            log.Warning("[Proteus] matcap: {0} ({1})", r.Outcome.Message, r.Outcome.Code);
            PayMatcapRedraw();
            return;
        }
        matcapRedrawOwed = false;   // AfterModChange below reloads and redraws

        var extra = new List<string>();
        if (r.Select is { Library: { } lib, Option: { } option })
        {
            var sel = MatcapSelection.Select(new PenumbraMatcapAdapter(penumbra), lib, option, r.ConfirmEnable);
            if (!sel.Ok) extra.Add(sel.Message);
            else if (!sel.Winning && sel.WinningFile != null) extra.Add(string.Format(s.NotWinningFmt, sel.WinningFile));
            matcapPanel.InvalidateStatus();
        }
        if (r.IsApply && r.GamePath != null && DrawnElsewhere(r.Root, r.GamePath, r.Outcome.Files) is { } drawn) extra.Add(drawn);
        // A face file the face overlay rewrites must be read again, not from its remembered copy (before the recomposite).
        // Only when a face model was written (Apply, Undo, Forget — a Change rewrites no model), for every face game path
        // the written files are published at (another race's path included).
        if (r.GamePath != null && MatcapPlan.TargetOf(r.GamePath, out _) is { Kind: MatcapPlan.Kind.Face } && !r.IsChange)
        {
            // The open path always (an Undo in a game face's edit mod has just removed its redirect), then every other.
            // After an undo that removed the file from its mod (a game face's edit mod, EDGE-59) nothing is remembered:
            // the overlay folds for one composite, then reads whatever really draws the face — a face mod or the game's.
            compositor.ForgetFaceUpstream(r.GamePath);
            var files = r.Outcome.Files.Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList();
            if (modelIndex >= 0 && modelIndex < models.Count) files.Add(models[modelIndex].File);
            foreach (var red in PenumbraModMeta.ReadAllRedirects(r.Root))
                if (files.Any(f => PenumbraModMeta.SameFile(f, red.File)) && MatcapPlan.TargetOf(red.GamePath, out _) is { Kind: MatcapPlan.Kind.Face })
                    compositor.ForgetFaceUpstream(red.GamePath, Path.Combine(r.Root, red.File.Replace('\\', Path.DirectorySeparatorChar)));
        }

        // The panel keeps one short line; the details go to chat, short; the log keeps the full message and file names.
        var (_, panel, chat) = MatcapPanelModel.Report(new MatcapPanelModel.JobReport(r.Outcome.Message, r.IsApply, r.ModelCount, r.RowHoles,
                                                                                      r.SiblingsSkipped, r.AlphaTested, r.Lod0Cut, extra, r.PortKept,
                                                                                      r.RowGlass, r.RowGlassTriangles));
        status = panel;
        statusIsError = false;
        matcapError = null;
        ticked.Clear();
        foreach (var line in chat) Plugin.ChatGui.Print("[Proteus] " + line);
        log.Information("[Proteus] matcap: {0}{1}{2}", r.Outcome.Message,
            r.SiblingsSkipped.Count > 0 ? " Skipped: " + string.Join(", ", r.SiblingsSkipped) + "." : "",
            extra.Count > 0 ? " " + string.Join(" ", extra) : "");
        AfterModChange(r.Root, r.Outcome.Files.Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList());
        // After AfterModChange (whose preview take-down clears the line): an Apply says whether it converted glass; an Undo
        // or Forget took any conversion away.
        matcapRowGlass = r.IsApply && r.RowGlass;
        matcapRowGlassTriangles = matcapRowGlass ? r.RowGlassTriangles : 0;
    }

    /// <summary>The warning when the character draws another file for this game path than the ones written.</summary>
    private string? DrawnElsewhere(string root, string gamePath, IReadOnlyList<string> written)
    {
        if (penumbra.ResolvePlayerPaths([gamePath]) is not { } resolved || !resolved.TryGetValue(gamePath, out var file) || file == null)
            return null;
        return MatcapPanelModel.DrawnStatus(root, gamePath, written, file, penumbra.GetModDirectory() ?? "", compositor.FaceUpstreamPath(gamePath));
    }
}
