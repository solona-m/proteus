using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Parts tab: pick geometry out of any installed mod's model and put it behind an on/off switch (an attribute
/// plus an IMC group written into the mod itself, so it works with Proteus turned off), or reshape it with a brush.
/// </summary>
public sealed class PartsPanel
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly PartViewport viewport;
    private readonly LiveBrush liveBrush;
    private readonly TextureLoader textureLoader;
    private readonly IPluginLog log;

    private Dictionary<string, string>? mods;
    private string modFilter = string.Empty;
    private string modelFilter = string.Empty;

    private string? modDir;

    /// <summary>Every redirect the mod publishes, unfiltered: the writer reads the item's variant off the material paths.</summary>
    private List<PenumbraModMeta.Redirect> redirects = [];

    /// <summary>Just the models, for the picker.</summary>
    private List<PenumbraModMeta.Redirect> models = [];

    /// <summary>What the picker shows for each entry of <see cref="models"/>, resolved once when the list is built, not per frame.</summary>
    private List<string> modelLabels = [];
    private int modelIndex = -1;

    private ModelParts? parts;

    /// <summary>How many switch letters this model has left, resolved when the model is read rather than per frame.</summary>
    private int freeLetters;
    private bool modelUnreadable;

    /// <summary>The picked mod is in Penumbra's pre-v4 layout, which Proteus reads but will not write —
    /// see <see cref="PenumbraModMeta.IsLegacyFolder"/>. Nothing below the mod picker is drawn for one.</summary>
    private bool modIsLegacy;

    private readonly HashSet<string> ticked = new(StringComparer.Ordinal);
    /// <summary>Submeshes whose islands are listed out. See <see cref="DrawPartRows"/>.</summary>
    private readonly HashSet<(int Mesh, int Submesh)> expanded = [];

    /// <summary>
    /// Parts locked against the brush, by label, per model (keyed like <see cref="ViewportKey"/>). A submesh's label
    /// covers all its islands. Kept for the session and never written into the mod.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> lockedParts = [];

    /// <summary>
    /// Parts Body size holds where their author put them, keyed like <see cref="lockedParts"/> but a separate set.
    /// Holding a buckle still through a refit says nothing about whether it may be brushed or moved by hand, so the
    /// two never share: a hold made here must not lock the part against Move afterwards.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> heldParts = [];

    /// <summary>For each vertex of <see cref="parts"/>, the index in its part list of the most specific part it
    /// belongs to (its island where the submesh lists islands, else the submesh); -1 for none.</summary>
    private int[] partOfVertex = [];

    /// <summary>The brush also paints its mirror image across the body's midline.</summary>
    private bool mirrorBrush;
    private string toggleName = string.Empty;
    private readonly List<(string Name, List<string> Parts)> pending = [];

    private MeshToggleRecord? existing;
    private string? status;
    private bool statusIsError;

    /// <summary>What a drag on the model does, and therefore which half of this tab is showing.</summary>
    private enum Tool
    {
        /// <summary>Pick parts and stage switches — everything this tab did before there was a brush.</summary>
        Navigate,

        /// <summary>Push the surface out along its normals, to clear a body poking through a garment.</summary>
        Inflate,

        /// <summary>The same brush pulling in, for a garment that stands too far off.</summary>
        Deflate,

        /// <summary>Smooth the surface under the brush — lumps the garment shipped with, or a rough pull.</summary>
        Relax,

        /// <summary>Stretch cloth straight across a hollow — a cleft or crease — instead of into it.</summary>
        Bridge,

        /// <summary>Paint how much the wind sways the garment — the red of its second vertex colour.</summary>
        Wind,

        /// <summary>Pick one part and drag it through space with a gizmo, the way Blender and 3ds Max move a selection.</summary>
        Move,

        /// <summary>Pick one part and turn it about its centre by dragging it sideways.</summary>
        Rotate,

        /// <summary>Pick one part and grow or shrink it about its centre by dragging it sideways.</summary>
        Scale,

        /// <summary>Refit the whole model onto another size of the body it was made for — see <see cref="BodyRetargetPanel"/>.</summary>
        Retarget,
    }

    /// <summary>The tools that work on one chosen part rather than painting: Move, Rotate and Scale share its choice.</summary>
    private static bool IsPartTool(Tool t) => t is Tool.Move or Tool.Rotate or Tool.Scale;

    private bool PartTool => IsPartTool(tool);

    /// <summary>
    /// The tools a drag on the model paints with. Written as its own predicate rather than "not Navigate", because
    /// Retarget is a third thing that is neither painting nor dragging, and every guard phrased as a single exclusion
    /// silently starts feeding strokes to the next tool added.
    /// </summary>
    private bool PaintTool => tool is Tool.Inflate or Tool.Deflate or Tool.Relax or Tool.Bridge or Tool.Wind;

    /// <summary>The handle Scale drags — the part itself — shared by the model view and the character.</summary>
    private readonly PartScaleDrag scaleDrag = new();

    /// <summary>The rings Rotate drags, shared by the model view and the character.</summary>
    private readonly RotateGizmo rotateGizmo = new();

    /// <summary>Pull out to start with: clearing a body poking through a garment is what the tab is opened for most.</summary>
    private Tool tool = Tool.Inflate;

    /// <summary>The handle the Move tool drags, shared by the model view and the character.</summary>
    private readonly TranslateGizmo moveGizmo = new();

    /// <summary>The part the Move tool has chosen, by label; null for none. Cleared with the model or the tool.</summary>
    private string? movePart;

    /// <summary>Move carries the cloth joined to the part along, fading out over <see cref="moveFalloffMm"/>.</summary>
    private bool moveAdjacent = true;

    /// <summary>
    /// Move, Rotate and Scale act on single polygons rather than whole parts. The selection is
    /// <see cref="movePolys"/>, and the soft falloff is measured along the surface through joined polygons.
    /// </summary>
    private bool movePolygons;

    /// <summary>The selected polygons in polygon mode, by their corners. Cleared with the model.</summary>
    private readonly HashSet<PolygonSelection.Key> movePolys = [];

    /// <summary>The open model's selectable polygons and which touch which; built on first use per model.</summary>
    private PolygonSelection? polySelection;

    /// <summary>Changes whenever <see cref="movePolys"/> does, for the live tint's cache.</summary>
    private int movePolysVersion;

    /// <summary>The polygon under the mouse in the model view this frame, in polygon mode; null when none.</summary>
    private PolygonSelection.Key? hoverPoly;

    /// <summary>How far along the surface the joined cloth follows a move, in millimetres.</summary>
    private float moveFalloffMm = 50f;

    /// <summary>
    /// The editable geometry of the model on screen, or null before one is picked. Rebuilt only when the model changes.
    /// </summary>
    private MeshVolumeSolve? volume;

    /// <summary>Brush radius and per-dab strength, both in millimetres.</summary>
    private float brushRadiusMm = 200f, brushStrengthMm = 0.4f;

    /// <summary>The bridge brush's own size, smaller than the others': a wide bridge spans past the hollow onto the curves beside it.</summary>
    private float bridgeRadiusMm = 70f;

    /// <summary>The size the current tool paints with; the bridge and wind brushes keep their own.</summary>
    private ref float ActiveRadiusMm
        => ref tool == Tool.Bridge ? ref bridgeRadiusMm
             : ref (tool == Tool.Wind ? ref windRadiusMm : ref brushRadiusMm);

    /// <summary>The open model's wind lookup for the viewer's wash, made once per model rather than per frame.</summary>
    private Func<int, float>? windAt;

    /// <summary>The wind brush's own size — see <see cref="windAmountPercent"/>.</summary>
    private float windRadiusMm = 15f;

    /// <summary>
    /// The wind being painted, as a percentage of full sway: each dab moves the surface toward it. Its own field so
    /// switching tools never reinterprets another brush's number.
    /// </summary>
    private float windAmountPercent = 5f;

    /// <summary>How much of the way to <see cref="windAmountPercent"/> each moment of painting goes.</summary>
    private float windRatePercent = 15f;

    /// <summary>
    /// The relax brush's strength, as a percentage moved toward the neighbours per moment of painting. Its own field:
    /// <see cref="brushStrengthMm"/> is a distance.
    /// </summary>
    private float relaxRatePercent = 30f;

    /// <summary>
    /// The bridge brush's rate, as a percentage of the remaining gap closed per moment of painting; far lower than
    /// relax's, since a bridge re-measures the gap each dab.
    /// </summary>
    private float bridgeRatePercent = 5f;

    /// <summary>How many models in this mod the brush has already written, so the way back can be offered
    /// only when there is something to go back from.</summary>
    private int brushSaved;

    /// <summary>
    /// The model's bytes as they were when the brush was opened on it. Every save is these plus the whole edit so far
    /// (see <see cref="MeshVolumeService.Apply"/>), so saving repeatedly never stacks.
    /// </summary>
    private byte[]? brushBase;

    /// <summary>When the brush last changed something not yet saved, as a tick count; -1 when nothing waits.</summary>
    private long brushChangedAt = -1;

    /// <summary>
    /// Paint in the model viewer in this window rather than on the character in the game world. Off by default.
    /// </summary>
    private bool showModelView;

    /// <summary>Where strokes come from this frame.</summary>
    private IBrushSurface Surface => showModelView ? viewport : liveBrush;

    /// <summary>Puts the edit on the character while painting on it — see <see cref="LiveBrushPreview"/>.</summary>
    private readonly LiveBrushPreview preview;

    /// <summary>The Body size tool's own half of the tab — see <see cref="BodyRetargetPanel"/>.</summary>
    private readonly BodyRetargetPanel retarget;

    /// <summary>The solve has changed since the character last showed it.</summary>
    private bool previewDirty;

    private long lastPreviewAt;

    /// <summary>
    /// Least time between previews while the brush is down; faster, the reloads queue and the character lags further behind.
    /// </summary>
    private const long PreviewIntervalMs = 150;

    public PartsPanel(
        PenumbraBridge penumbra, CompositorService compositor, PartViewport viewport, LiveBrush liveBrush,
        TextureLoader textureLoader, UVRemapService uvRemap, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.viewport = viewport;
        this.liveBrush = liveBrush;
        preview = new LiveBrushPreview(penumbra, compositor, log);
        retarget = new BodyRetargetPanel(penumbra, uvRemap, log);
        LiveBrushPreview.CleanUp();
        partOfVertexFn = PartOfVertex;
        lockClickedFn = LockClickedOnCharacter;
        tickClickedFn = TickClickedOnCharacter;
        partTickedFn = PartTicked;
        partHeldFn = PartHeld;
        moveClickedFn = MoveClickedOnCharacter;
        polyClickedFn = PolygonClicked;
        moveTickedFn = MoveTicked;
        gizmoCaptureFn = () => tool switch
        {
            Tool.Move   => moveGizmo.Capturing,
            Tool.Rotate => rotateGizmo.Capturing,
            _           => scaleDrag.Capturing,
        };
        this.textureLoader = textureLoader;
        this.log = log;
    }


    /// <summary>Drop the mod list so the next frame re-reads it — wired to the window's Refresh.</summary>
    public void Refresh() => mods = null;

    /// <summary>Every mod Penumbra knows, by folder → display name, less Proteus's own output mod.</summary>
    private Dictionary<string, string> LoadMods()
        => (penumbra.GetAllMods() ?? [])
            .Where(m => !string.Equals(m.Key, SidecarDiscoveryService.ManagedModDir,
                                       StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.Key, m => m.Value);

    /// <summary>Whether the last <see cref="Draw"/> drew the model viewer; the window grows itself when this turns on.</summary>
    public bool ShowingModel { get; private set; }

    /// <param name="fillHeight">Whether the model row may take all the height that is left. True only while
    /// the window is user-resizable.</param>
    /// <param name="reserveBelow">Height the window itself still needs under the tab content (its footer).</param>
    public void Draw(bool fillHeight, float reserveBelow)
    {
        frame.Begin();
        try
        {
            DrawCore(fillHeight, reserveBelow);
        }
        finally
        {
            frame.End(log, "Studio");
        }
    }

    /// <summary>Where the Studio's frame time goes — logged only for a frame slow enough to stall the game.</summary>
    private readonly FrameTimer frame = new();

    private void DrawCore(bool fillHeight, float reserveBelow)
    {
        var ps = Strings.Parts;
        ShowingModel = false;
        TickAutosave();
        frame.Mark("autosave");
        ConsumeApplySizes();
        frame.Mark("apply sizes");

        // Asked for mid-frame by a refit's save or undo; done here, before anything this frame reads the model.
        if (refreshModelsPending)
        {
            refreshModelsPending = false;
            RefreshModels();
            frame.Mark("refresh models");
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(ps.Intro);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (modDir == null && !autoPicked)
        {
            autoPicked = true;
            AutoPickWorn();
            frame.Mark("auto-pick worn");
        }

        DrawLivePick();
        frame.Mark("live pick");
        DrawModPicker();
        frame.Mark("mod picker");
        if (modDir == null) return;

        if (modIsLegacy)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, ps.LegacyMod);
            ImGui.PopTextWrapPos();

            // Undo still offered: a legacy folder may carry switches an older Proteus wrote.
            DrawExisting();
            DrawStatus();
            return;
        }

        DrawExisting();
        DrawStatus();
        frame.Mark("existing + status");
        DrawModelPicker();
        frame.Mark("model picker");
        if (modelIndex < 0) return;

        if (modelUnreadable)
        {
            ImGui.TextColored(ProteusStyle.Warn, ps.Unreadable);
            return;
        }
        if (parts == null) return;

        ImGui.Separator();

        // Fill mode is stable only while the window is NOT auto-resizing; under AlwaysAutoResize the same expression grows
        // without bound, so non-fill frames keep a fixed height.
        // Fitting: the side panel's content measured last frame, plus the 4 px the fill branch takes off.
        float height = MathF.Max(ProteusStyle.S(360f), sidePanelContent + ProteusStyle.S(4f));
        if (fillHeight)
            height = MathF.Max(ImGui.GetContentRegionAvail().Y - reserveBelow - ProteusStyle.S(4f),
                               ProteusStyle.S(200f));

        // The window grows for the viewer only; controls alone fit the size it already is.
        ShowingModel = showModelView;
        HandleUndoShortcut();
        HandleBrushSizeKeys();

        // The tools and their controls down the left, beside the model; one set of controls or the other, never both.
        using (var side = ImRaii.Child("##partsSide", new Vector2(ProteusStyle.S(SidePanelWidth), height), true))
        {
            if (side)
            {
                if (ImGui.Checkbox(Strings.Parts.ShowModelView, ref showModelView))
                {
                    FlushPending();   // a stroke's pending save belongs to the surface it was painted on
                    EndLivePreview(refreshGame: true);
                    viewport.Recolour();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.Parts.ShowModelViewTip);
                ImGui.Separator();

                DrawToolPicker();
                ImGui.Separator();
                frame.Mark("tool picker");
                if (tool == Tool.Navigate) DrawStaging();
                else if (tool == Tool.Retarget) DrawRetarget();
                else if (PartTool) DrawMove();
                else DrawBrush();
                frame.Mark($"tool {tool}");

                // Window-local, so it already counts any scroll; plus the panel's bottom padding and border.
                sidePanelContent = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y + 2f;
            }
        }

        if (showModelView)
        {
            ImGui.SameLine();
            DrawParts(height);
            frame.Mark("model view");
        }
        else
        {
            ImGui.SameLine();
            DrawPartList(parts, height);
            frame.Mark("part list");

            // Under Toggle Parts a click on the open garment ticks the part under it; a click on another worn garment opens that one.
            bool pickParts = tool == Tool.Navigate;
            bool moving = PartTool;
            if (volume != null && brushBase != null && ModRoot() is { } root)
                liveBrush.ArmBrush(Path.Combine(root, models[modelIndex].File.Replace('/', Path.DirectorySeparatorChar)),
                                   brushBase, volume, ActiveRadiusMm / 1000f, showWind: tool == Tool.Wind,
                                   mirror: mirrorBrush, partOf: partOfVertexFn,
                                   lockClicked: moving ? moveClickedFn : pickParts ? tickClickedFn : lockClickedFn,
                                   pickParts: pickParts,
                                   partTicked: moving ? moveTickedFn : partTickedFn,
                                   tickedVersion: moving ? (movePolygons ? movePolysVersion : MoveVersion()) : TickedVersion(),
                                   moveGizmo: tool == Tool.Move ? moveGizmo : null,
                                   movePivot: moving ? MovePivot() : null,
                                   graftedGamePath: GraftedGamePath(),
                                   scaleDrag: tool == Tool.Scale ? scaleDrag : null,
                                   rotateGizmo: tool == Tool.Rotate ? rotateGizmo : null,
                                   partLocked: tool == Tool.Retarget ? partHeldFn : null,
                                   lockedVersion: tool == Tool.Retarget ? VersionOf(RetargetHolds) : 0,
                                   polygons: moving && movePolygons ? movePolys : null,
                                   polygonClicked: moving && movePolygons ? polyClickedFn : null);
        }

        frame.Mark("arm live brush");
        PumpMove();
        PumpBrush();
        frame.Mark("move + brush");
        TickLivePreview();
        frame.Mark("live preview");
    }

    /// <summary>
    /// Put the edit on the character when it has changed: throttled while the brush is down, at once when it is
    /// not (the settled stroke, an undo). Painting in the viewer needs none of this — the viewer is its preview.
    /// </summary>
    private void TickLivePreview()
    {
        if (showModelView || !previewDirty || preview.Busy) return;
        if (volume == null || brushBase == null || modelIndex < 0) { previewDirty = false; return; }
        bool customizePart = TargetIsCustomizePart;
        // An imported piece is drawn inside a Proteus shell, so there is no game path to preview on; the save shows it.
        if (TargetIsContent) { previewDirty = false; return; }

        // A part that does not reload in place: show the finished stroke once the brush is up, preview taken down first.
        if (preview.UnsupportedFor(customizePart))
        {
            if (Editing) return;
            previewDirty = false;
            if (preview.Active) preview.End(redraw: true);
            else compositor.RedrawForChangedModel();
            return;
        }

        long now = Environment.TickCount64;
        if (Editing && now - lastPreviewAt < PreviewIntervalMs) return;

        byte[] bytes;
        try { bytes = MeshVolumeService.Inflate(brushBase, volume).Model; }
        catch (Exception ex)
        {
            log.Warning("[Proteus] live brush: preview could not build the model: {0}", ex.Message);
            previewDirty = false;
            return;
        }

        // Every game path the mod points at this file — the one the character is drawing is among them.
        var file = models[modelIndex].File.Replace('\\', '/');
        var gamePaths = redirects
            .Where(r => string.Equals(r.File.Replace('\\', '/'), file, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GamePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (preview.Push(bytes, gamePaths, customizePart))
        {
            previewDirty = false;
            lastPreviewAt = now;
        }
    }

    /// <summary>A brush stroke or a move drag is under way — the edit is still changing under the user's hand.</summary>
    private bool Editing => Surface.Painting || volume is { Moving: true };

    /// <summary>The model being edited is hair, face, ears or tail rather than gear — reloaded a different way.</summary>
    private bool TargetIsCustomizePart
        => modelIndex >= 0 && modelIndex < models.Count && LiveBrushPreview.IsCustomizePart(models[modelIndex].GamePath);

    /// <summary>Take the preview off the character, so it draws the mod's own (saved) file again.</summary>
    private void EndLivePreview(bool refreshGame)
    {
        previewDirty = false;
        preview.End(redraw: refreshGame);
    }

    /// <summary>The tab is being left, closed or torn down: save anything waiting and take the preview down.</summary>
    /// <param name="refreshGame">False on teardown, when nothing should be poked beyond landing the file.</param>
    public void Leave(bool refreshGame = true)
    {
        FinishMove();   // a drag cut off by leaving still counts, and still undoes
        FlushPending(refreshGame);
        EndLivePreview(refreshGame);
        retarget.Clear();     // its preview has just been taken down, so its plan no longer matches what is drawn
        autoPicked = false;   // the next visit may find different gear on
    }

    /// <summary>
    /// Choosing a garment by clicking it on the character: on while painting on the character with Toggle Parts
    /// selected or nothing open to brush yet. Off under a brush with a model open, so a stroke cannot swap the model.
    /// </summary>
    private void DrawLivePick()
    {
        if (showModelView || penumbra.GetModDirectory() is not { } modsRoot) return;
        if (tool != Tool.Navigate && tool != Tool.Retarget && volume != null) return;
        liveBrush.ArmPick(modsRoot, OnLivePicked);
        ImGui.TextDisabled(Strings.Parts.LivePickTip);
        ImGui.Spacing();
    }

    /// <summary>
    /// Open the model of the chosen mod that the character is wearing, in <see cref="AutoPickWorn"/>'s slot order.
    /// Nothing is chosen when the mod is not being worn.
    /// </summary>
    private void SelectWornModel()
    {
        if (modDir == null || models.Count == 0) return;
        var worn = WornFiles()
            .Where(w => string.Equals(w.Mod, modDir, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Rel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (worn.Count == 0) return;

        int best = -1, bestRank = int.MaxValue;
        for (int i = 0; i < models.Count; i++)
        {
            if (!worn.Contains(models[i].File.Replace('\\', '/'))) continue;
            int rank = Array.FindIndex(AutoPickSlots, s => models[i].GamePath.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            if (rank < 0) rank = AutoPickSlots.Length;
            if (rank < bestRank) { best = i; bestRank = rank; }
        }
        if (best >= 0) SelectModel(best);
    }

    /// <summary>Tried once per visit to the tab, so a choice the user clears is not forced back on them.</summary>
    private bool autoPicked;

    /// <summary>Which worn slot to open first when the tab is entered with nothing chosen: body, then legs.</summary>
    private static readonly string[] AutoPickSlots = ["_top.mdl", "_dwn.mdl", "_glv.mdl", "_sho.mdl", "_met.mdl"];

    /// <summary>
    /// Open the garment most likely to need a fix: the worn chest piece if it comes from a mod, else legs, hands, feet
    /// and head, at the exact file the character is wearing.
    /// </summary>
    private void AutoPickWorn()
    {
        if (penumbra.GetModDirectory() is not { } root
            || penumbra.GetActivePlayerModelFiles() is not { } files
            || penumbra.GetActivePlayerModelGamePaths() is not { } gamePaths)
            return;

        string? best = null;
        int bestRank = int.MaxValue;
        foreach (var file in files)
        {
            if (!gamePaths.TryGetValue(BodyShapeReader.PathKey(file), out var gamePath)) continue;
            int rank = Array.FindIndex(AutoPickSlots, s => gamePath.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            if (rank < 0 || rank >= bestRank) continue;
            if (!HatCompatService.InMods(file, root, out var modRoot, out _)) continue;
            if (string.Equals(Path.GetFileName(modRoot), SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
                continue;
            best = file;
            bestRank = rank;
        }
        if (best != null) OnLivePicked(best);
    }

    /// <summary>A garment was clicked on the character (or chosen for them on entry): open its mod and model.</summary>
    private void OnLivePicked(string file)
    {
        if (penumbra.GetModDirectory() is not { } modsRoot
            || !HatCompatService.InMods(file, modsRoot, out var modRoot, out var rel))
            return;

        var dir = Path.GetFileName(modRoot);
        // Our own output mod is rebuilt on every composite, so edits there are thrown away; the clicked shell's garment is
        // edited in the mod it was imported from.
        if (string.Equals(dir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
        {
            status = Strings.Parts.LivePickedShell;
            statusIsError = false;
            return;
        }
        if (!string.Equals(dir, modDir, StringComparison.OrdinalIgnoreCase)) SelectMod(dir);

        var wanted = rel.Replace('\\', '/');
        int index = models.FindIndex(m => string.Equals(m.File.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            status = string.Format(Strings.Parts.LivePickedNotListedFmt, Path.GetFileName(file));
            statusIsError = true;
            return;
        }
        if (index != modelIndex) SelectModel(index);
    }

    /// <summary>Width of the tool panel left of the model, before UI scaling.</summary>
    private const float SidePanelWidth = 250f;

    /// <summary>How tall the side panel's controls came out last frame, scaled.</summary>
    private float sidePanelContent;

    /// <summary>How tall the tab's controls need to be, so the window can fit itself again when they grow.</summary>
    public float ControlsHeight => sidePanelContent;

    /// <summary>
    /// Ctrl+Z takes back the last brush stroke, saving and redrawing at once. Only while this tab has the keyboard
    /// (see <see cref="ShortcutsHaveTheKeyboard"/>), never mid-stroke and never while picking parts.
    /// </summary>
    private void HandleUndoShortcut()
    {
        bool pressed = undoKey.Poll();   // every frame — see HeldKey
        if (!pressed || !PaintTool || volume is not { CanUndo: true } vol || Editing) return;
        var io = ImGui.GetIO();
        if (!io.KeyCtrl || !ShortcutsHaveTheKeyboard()) return;

        vol.Undo();
        AfterBrushEdit();
        SaveBrush();
    }

    private readonly HeldKey undoKey = new(KeyPoll.VkZ, repeat: false);
    private readonly HeldKey growKey = new(KeyPoll.VkRightBracket, repeat: true);
    private readonly HeldKey shrinkKey = new(KeyPoll.VkLeftBracket, repeat: true);

    /// <summary>
    /// Whether a key pressed now is meant for this tab: the game is foreground, no text box has the keyboard, and
    /// this window is focused or hovered, or (painting on the character) no window is focused at all.
    /// </summary>
    private bool ShortcutsHaveTheKeyboard()
    {
        if (ImGui.GetIO().WantTextInput || !KeyPoll.GameHasFocus()) return false;
        return ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            || ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)
            || (!showModelView && !ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow));
    }

    /// <summary>The brush size slider's range, in millimetres — down to a millimetre for sculpting a face.</summary>
    private const float MinBrushMm = 1f, MaxBrushMm = 300f;

    /// <summary>
    /// <c>[</c> and <c>]</c> shrink and grow the current tool's brush by a proportion, Shift for fine steps; held,
    /// they repeat. Gated by <see cref="ShortcutsHaveTheKeyboard"/>.
    /// </summary>
    private void HandleBrushSizeKeys()
    {
        bool grow = growKey.Poll(), shrink = shrinkKey.Poll();   // every frame — see HeldKey
        if (!grow && !shrink) return;
        if (!PaintTool || volume == null) return;
        var io = ImGui.GetIO();
        if (io.KeyCtrl || !ShortcutsHaveTheKeyboard()) return;

        float step = io.KeyShift ? 1.03f : 1.15f;
        float size = ActiveRadiusMm;
        if (grow) size *= step;
        if (shrink) size /= step;
        size = Math.Clamp(size, MinBrushMm, MaxBrushMm);
        if (size == ActiveRadiusMm) return;

        ActiveRadiusMm = size;
        viewport.Recolour();
    }

    /// <summary>A button size spanning the rest of the current row at the normal button height.</summary>
    private static Vector2 FullWidth() => new(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight());

    // ── pickers ─────────────────────────────────────────────────────────────

    private void DrawModPicker()
    {
        var ps = Strings.Parts;
        // Proteus's own output mod is left out: it is rebuilt on every composite, so an edit written into it does not last.
        mods ??= LoadMods();

        var width = ProteusStyle.S(340f);
        ImGui.SetNextItemWidth(width);

        // BeginCombo applies its own row limit only when no size constraint is given, so the height cap must be explicit.
        var popupMaxH = ImGui.GetTextLineHeightWithSpacing() * 18 + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width * 2.2f, popupMaxH));

        var current = modDir != null && mods.TryGetValue(modDir, out var name) ? name : ps.PickMod;
        if (!ImGui.BeginCombo(ps.Mod + "##partsMod", current)) return;

        // SetKeyboardFocusHere targets the NEXT item submitted, so it has to sit immediately before it.
        bool appearing = ImGui.IsWindowAppearing();
        if (appearing)
        {
            modFilter = "";
            // Once per open, not per frame: an IPC round trip over every loaded resource.
            equippedMods = EquippedModDirectories();
            // The mod list too, so a mod installed since the tab was first drawn is listed.
            mods = LoadMods();
        }
        ImGui.SetNextItemWidth(-1);
        if (appearing) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##partsFilter", Strings.Export.FilterHint, ref modFilter, 64);
        ImGui.Separator();

        // Worn mods first, then everything else, each alphabetical.
        int shown = 0;
        bool anyEquippedShown = false, separated = false;
        foreach (var (dir, label) in mods
                     .OrderBy(m => equippedMods.Contains(m.Key) ? 0 : 1)
                     .ThenBy(m => m.Value, StringComparer.OrdinalIgnoreCase))
        {
            // Folder as well as name: the two routinely differ.
            if (modFilter.Length > 0
                && label?.Contains(modFilter, StringComparison.OrdinalIgnoreCase) != true
                && dir?.Contains(modFilter, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            bool worn = equippedMods.Contains(dir);
            if (worn) anyEquippedShown = true;
            else if (anyEquippedShown && !separated) { ImGui.Separator(); separated = true; }

            shown++;
            // ##dir: two mods can share a display name, and duplicate ImGui ids would route the click to the wrong row.
            bool picked;
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Ok, worn))
                picked = ImGui.Selectable($"{label}##{dir}", dir == modDir);
            if (picked && dir != modDir)
            {
                SelectMod(dir);
                SelectWornModel();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(dir);
        }
        if (shown == 0)
            ImGui.TextDisabled(string.Format(Strings.Export.NoMatchFmt, modFilter));

        ImGui.EndCombo();
    }

    /// <summary>Mod folders supplying a model the character is drawing right now — see
    /// <see cref="EquippedModDirectories"/>. Refreshed each time the mod picker opens.</summary>
    private HashSet<string> equippedMods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which installed mods the character is wearing, by folder: every mod a loaded model file is read from.
    /// Empty rather than null when Penumbra cannot answer.
    /// </summary>
    private HashSet<string> EquippedModDirectories()
    {
        var worn = WornFiles().Select(w => w.Mod).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // An imported garment is drawn out of our output mod; the composite records which mods it cut content from.
        foreach (var dir in (IEnumerable<string>?)mods?.Keys ?? [])
            if (compositor.GetLiveContentModels(dir) is { Count: > 0 })
                worn.Add(dir);
        return worn;
    }

    /// <summary>Files inside this mod that the character is drawing, relative to the mod root with forward
    /// slashes — the spelling <see cref="PenumbraModMeta.Redirect.File"/> is compared in. Refreshed each
    /// time the model picker opens.</summary>
    private HashSet<string> wornModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every loaded model file that lives in a mod, as the mod's folder name and the file's path inside it.
    /// Empty when Penumbra cannot answer, so both pickers fall back to plain lists.
    /// </summary>
    private List<(string Mod, string Rel)> WornFiles()
    {
        var found = new List<(string, string)>();
        if (penumbra.GetModDirectory() is not { } root || penumbra.GetActivePlayerModelFiles() is not { } files)
            return found;

        foreach (var file in files)
            if (HatCompatService.InMods(file, root, out var modRoot, out var rel))
                found.Add((Path.GetFileName(modRoot), rel));

        // Imported models worn through our shell, which the walk above cannot see. See CompositorService.GetLiveContentModels.
        if (modDir != null && compositor.GetLiveContentModels(modDir) is { } content)
            foreach (var rel in content)
                found.Add((modDir, rel));
        return found;
    }

    private void SelectMod(string dir)
    {
        FinishMove();
        FlushPending();   // before modDir changes, which the save needs to find the file
        EndLivePreview(refreshGame: true);
        brushChangedAt = -1;
        movePart = null;
        ClearPolygons(forgetModel: true);
        ReleaseHandles();
        retarget.Clear();
        modDir = dir;
        modelIndex = -1;
        parts = null;
        modelUnreadable = false;
        modIsLegacy = false;
        ticked.Clear();
        expanded.Clear();
        pending.Clear();

        models = [];
        modelLabels = [];
        redirects = [];
        status = null;
        var root = ModRoot();
        if (root == null) return;

        existing = MeshToggleService.ReadRecord(root);
        brushSaved = MeshVolumeService.PatchedCount(root);

        // A pre-v4 folder is read-only to Proteus, so nothing is listed. No wait on the draw thread; a write still checks.
        modIsLegacy = PenumbraModMeta.IsLegacyFolder(root, waitIfHeld: false);
        if (modIsLegacy) return;

        // Models the mod PUBLISHES: a published model carries the game path its item's IMC identity is read from.
        redirects = PenumbraModMeta.ReadAllRedirects(root);

        // Grouped by item, but within an item kept in declaration order (the author's option order); OrderBy is stable.
        models = redirects
            .Where(r => r.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An imported pack's garments publish nothing, so they are listed from the sidecar, after the published ones.
        models.AddRange(ContentModels(root, contentFiles));
        modelLabels = ModelLabels(models);
    }

    /// <summary>Set by a refit's save or undo; <see cref="Draw"/> re-reads the model list at the top of the next frame.</summary>
    private bool refreshModelsPending;

    /// <summary>
    /// Re-read the open mod's model list after something added or removed a model — a saved or undone body refit —
    /// keeping the open model open. Unlike <see cref="SelectMod"/>, nothing else is reset: the model, its brush state
    /// and its locks all stay, because the model itself did not change.
    /// </summary>
    private void RefreshModels()
    {
        if (ModRoot() is not { } root || modIsLegacy) return;
        var open = modelIndex >= 0 && modelIndex < models.Count ? models[modelIndex] : (PenumbraModMeta.Redirect?)null;

        redirects = PenumbraModMeta.ReadAllRedirects(root);
        models = redirects
            .Where(r => r.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        models.AddRange(ContentModels(root, contentFiles));
        modelLabels = ModelLabels(models);

        // The same row by file and game path; the list may have grown ahead of it. Gone (an undone refit that was
        // open) falls back to nothing open, as opening the mod afresh would.
        modelIndex = open is { } was
            ? models.FindIndex(m => string.Equals(m.File, was.File, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(m.GamePath, was.GamePath, StringComparison.OrdinalIgnoreCase)
                                    && m.Source == was.Source)
            : -1;
        if (modelIndex < 0 && open != null)
        {
            // Everything tied to the model that went, as opening another model would drop it.
            FinishMove();
            EndLivePreview(refreshGame: true);
            parts = null;
            volume = null;
            brushBase = null;
            movePart = null;
            ClearPolygons(forgetModel: true);
            ReleaseHandles();
            retarget.Clear();
            viewport.Clear();
        }
    }

    /// <summary>
    /// Files this mod's models come from that Penumbra does not publish (an imported pack's content pieces);
    /// <paramref name="files"/> is filled with them. The game path is derived from the file's own name.
    /// </summary>
    private static List<PenumbraModMeta.Redirect> ContentModels(string root, HashSet<string> files)
    {
        files.Clear();
        var rows = new List<PenumbraModMeta.Redirect>();
        if (SidecarDiscoveryService.TryReadMetadata(root) is not { } meta) return rows;

        void Add(ContentPiece piece, string source)
        {
            foreach (var rel in piece.ModelFiles())
            {
                if (!files.Add(rel)) continue;   // one piece shipped under several options
                if (ContentGamePath(rel) is not { } gamePath) continue;
                rows.Add(new PenumbraModMeta.Redirect(gamePath, rel.Replace('\\', '/'), source));
            }
        }

        foreach (var piece in meta.Content ?? []) Add(piece, "");
        foreach (var group in meta.ContentGroups ?? [])
            foreach (var option in group.Options)
                foreach (var piece in option.Pieces)
                    Add(piece, $"{group.PenumbraGroupName} / {option.Name}");
        return rows;
    }

    /// <summary>
    /// The game path a content model's file name names — <c>chara/equipment/e0041/model/c0201e0041_top.mdl</c>
    /// — or null when the name is not one the game could ask for.
    /// </summary>
    internal static string? ContentGamePath(string modelFile)
    {
        var leaf = Path.GetFileName(modelFile.Replace('\\', '/'));
        if (ContentSlot.Parse(leaf) is not { } p || p.SetTag.Length < 2) return null;
        var tree = p.SetTag[0] == 'a' ? "accessory" : "equipment";
        return $"chara/{tree}/{p.SetTag}/model/{leaf}";
    }

    /// <summary>Model files of this mod that Proteus grafts rather than Penumbra publishing them.</summary>
    private readonly HashSet<string> contentFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The open model is an imported piece — worn through a Proteus shell, not as a file of its own.</summary>
    private bool TargetIsContent
        => modelIndex >= 0 && modelIndex < models.Count
        && contentFiles.Contains(models[modelIndex].File);

    /// <summary>
    /// The path to pose the open model at when the character is wearing it through a Proteus shell. Null for an ordinary
    /// model, and for an imported variant the character is NOT wearing (posing it would paint a surface nobody wears).
    /// </summary>
    private string? GraftedGamePath()
    {
        if (!TargetIsContent || modDir == null) return null;
        var file = models[modelIndex].File.Replace('\\', '/');
        return compositor.GetLiveContentModels(modDir) is { } live && live.Contains(file)
            ? models[modelIndex].GamePath
            : null;
    }

    private void DrawModelPicker()
    {
        var ps = Strings.Parts;
        if (models.Count == 0)
        {
            ImGui.TextDisabled(ps.NoModels);
            return;
        }

        ImGui.SetNextItemWidth(ProteusStyle.S(340f));
        var current = modelIndex >= 0 ? modelLabels[modelIndex] : ps.PickModel;
        if (ImGui.BeginCombo(ps.Model + "##partsModel", current))
        {
            // Once per open, for the same reason as the mod picker: an IPC walk of everything loaded.
            if (ImGui.IsWindowAppearing())
                wornModels = WornFiles().Where(w => string.Equals(w.Mod, modDir, StringComparison.OrdinalIgnoreCase))
                                        .Select(w => w.Rel)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A mod with a size group lists a model per size, and a mod with a size group per body lists dozens.
            ComboSearch.Box("##partsModel", ref modelFilter);
            bool any = false;
            for (int i = 0; i < models.Count; i++)
            {
                if (!ComboSearch.Matches(modelFilter, modelLabels[i] + " " + models[i].File)) continue;
                any = true;
                // Green for the file the character is drawing right now.
                bool worn = wornModels.Contains(models[i].File.Replace('\\', '/'));
                bool picked;
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Ok, worn))
                    picked = ImGui.Selectable(modelLabels[i] + "##m" + i, i == modelIndex);
                if (picked && i != modelIndex) SelectModel(i);
            }
            if (!any) ImGui.TextDisabled(Strings.Parts.NoMatches);
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// What one model is called in the picker: the mod's own option label leads; the slot is appended only to break
    /// a tie (see <see cref="ModelLabels"/>).
    /// </summary>
    internal static string ModelLabel(PenumbraModMeta.Redirect r)
        => r.Source.Length > 0 ? r.Source : SlotOf(r);

    /// <summary>The slot and set a model path names — "Legs — e0488" — or its file name if it names neither.</summary>
    internal static string SlotOf(PenumbraModMeta.Redirect r)
        => ContentSlot.Parse(r.GamePath) is { } p ? $"{p.Label} — {p.SetTag}" : Path.GetFileName(r.GamePath);

    /// <summary>
    /// One label per model; the slot is appended to every member of a set sharing a label, and to nothing else.
    /// </summary>
    internal static List<string> ModelLabels(IReadOnlyList<PenumbraModMeta.Redirect> models)
    {
        var labels = models.Select(ModelLabel).ToList();
        var clashes = labels.GroupBy(l => l, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        for (int i = 0; i < labels.Count; i++)
            if (clashes.Contains(labels[i]))
                labels[i] = $"{labels[i]}  ({SlotOf(models[i])})";
        return labels;
    }

    private void SelectModel(int index)
    {
        // A pending edit belongs to the model being replaced, so the waiting flag is dropped even if its save fails.
        FinishMove();
        FlushPending();
        EndLivePreview(refreshGame: true);
        brushChangedAt = -1;
        brushBase = null;
        movePart = null;
        ClearPolygons(forgetModel: true);
        ReleaseHandles();
        retarget.Clear();
        // A new solve starts from the file as it is now, so the other sizes must too. Replaced, not cleared — see sizeBases.
        sizeBases = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        modelIndex = index;
        ticked.Clear();
        expanded.Clear();
        freeLetters = 0;
        // Staged switches name parts by label, and a label means something different on another model.
        pending.Clear();
        parts = null;
        modelUnreadable = false;

        var root = ModRoot();
        if (root == null) { modelUnreadable = true; return; }

        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(root,
                models[index].File.Replace('/', Path.DirectorySeparatorChar)));
            parts = ModelPartReader.Read(bytes);
            brushBase = bytes;
            modelUnreadable = parts == null;
            freeLetters = parts == null ? 0 : ModelPartReader.FreeLetters(parts.AttributeNames).Count;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] parts: could not read {0}", models[index].File);
            modelUnreadable = true;
        }
        // The brush state is per vertex, so it goes with the model.
        volume = parts != null ? new MeshVolumeSolve(parts) : null;
        windAt = volume != null ? volume.WindAt : null;
        viewport.PositionOverride = null;
        partOfVertex = parts != null ? BuildPartOfVertex(parts) : [];
        ApplyLocks();

        if (parts != null) viewport.Show(ViewportKey, parts);
        else viewport.Clear();
    }

    // ── locked parts ────────────────────────────────────────────────────────

    private static readonly HashSet<string> NoSelection = [];

    /// <summary>
    /// The open model's locked labels for the tool in use: Body size's holds under Body size, the brush locks under
    /// every other tool. Everything that draws, toggles or tests a lock goes through this, so the rows, the model view
    /// and a click on the character all follow the tool without knowing there are two sets.
    /// </summary>
    private HashSet<string> Locks => LockSet(tool == Tool.Retarget ? heldParts : lockedParts);

    /// <summary>The open model's brush locks whatever the tool — what the brush solve is always given.</summary>
    private HashSet<string> BrushLocks => LockSet(lockedParts);

    /// <summary>The open model's holds for Body size whatever the tool.</summary>
    private HashSet<string> RetargetHolds => LockSet(heldParts);

    /// <summary>One set of labels for the open model, created on first use.</summary>
    private HashSet<string> LockSet(Dictionary<string, HashSet<string>> byModel)
    {
        if (!byModel.TryGetValue(ViewportKey, out var set))
            byModel[ViewportKey] = set = new HashSet<string>(StringComparer.Ordinal);
        return set;
    }

    private static int[] BuildPartOfVertex(ModelParts model)
    {
        var result = new int[model.Positions.Length / 3];
        Array.Fill(result, -1);
        // Submeshes first, then islands over them: the finer part wins, as it does for a click in the viewer.
        foreach (bool islands in new[] { false, true })
            for (int p = 0; p < model.Parts.Count; p++)
            {
                if (model.Parts[p].Island >= 0 != islands) continue;
                foreach (int v in model.Parts[p].Triangles)
                    if (v >= 0 && v < result.Length) result[v] = p;
            }
        return result;
    }

    /// <summary>The submesh row an island belongs to, or null for a submesh (or an island without one).</summary>
    private ModelPart? ParentOf(ModelPart part)
        => part.Island < 0 ? null
         : parts?.Parts.FirstOrDefault(p => p.Island < 0 && p.Mesh == part.Mesh && p.Submesh == part.Submesh);

    private bool IsLocked(ModelPart part)
        => Locks.Contains(part.Label) || (ParentOf(part) is { } parent && Locks.Contains(parent.Label));

    private static bool IsSkin(ModelPart part) => SecondSkinWriter.IsBodySkinMaterial(part.Material);

    /// <summary>Lock or unlock one part, from the list, a Shift-click on the model or one on the character.</summary>
    private void ToggleLock(string label)
    {
        if (parts is not { } model || model.Parts.FirstOrDefault(p => p.Label == label) is not { } part) return;
        // A skin lock means nothing to the brush, which never moves skin; only the refit, which does, offers one.
        if (IsSkin(part) && tool != Tool.Retarget) return;
        var locks = Locks;

        if (!IsLocked(part))
        {
            locks.Add(label);
            // A whole submesh locked covers its islands; their own entries would only linger after it is unlocked.
            if (part.Island < 0)
                foreach (var island in model.Parts.Where(p => p.Island >= 0 && p.Mesh == part.Mesh && p.Submesh == part.Submesh))
                    locks.Remove(island.Label);
        }
        else if (!locks.Remove(label) && ParentOf(part) is { } parent)
        {
            // Unlocking one island of a locked submesh: the submesh gives way to every other island of it.
            locks.Remove(parent.Label);
            foreach (var sibling in model.Parts.Where(p => p.Island >= 0 && p.Mesh == part.Mesh
                                                        && p.Submesh == part.Submesh && p.Label != label))
                locks.Add(sibling.Label);
        }

        ApplyLocks();
    }

    private void UnlockAll()
    {
        Locks.Clear();
        ApplyLocks();
    }

    /// <summary>Hand the locks to the solve and the viewer. Called again when the tool changes, since the viewer
    /// shows the active tool's set.</summary>
    private void ApplyLocks()
    {
        if (parts == null) return;
        viewport.Locked = Locks;
        viewport.Recolour();
        if (volume == null) return;
        var locks = BrushLocks;
        // A submesh's triangles already include every island of it, so the labels alone are enough.
        volume.SetLocked(parts.Parts.Where(p => locks.Contains(p.Label)).SelectMany(p => p.Triangles));
    }

    /// <summary>A Shift-click on the character, with a vertex of the triangle it landed on.</summary>
    private void LockClickedOnCharacter(int vertex)
    {
        if (parts == null || vertex < 0 || vertex >= partOfVertex.Length || partOfVertex[vertex] < 0) return;
        ToggleLock(parts.Parts[partOfVertex[vertex]].Label);
    }

    private int PartOfVertex(int vertex) => vertex >= 0 && vertex < partOfVertex.Length ? partOfVertex[vertex] : -1;

    // Made once and handed to the live brush every frame, rather than a new delegate per frame per method group.
    private readonly Func<int, int> partOfVertexFn;
    private readonly Action<int> lockClickedFn;
    private readonly Action<int> tickClickedFn;
    private readonly Func<int, bool> partTickedFn;
    private readonly Func<int, bool> partHeldFn;

    /// <summary>Whether part <paramref name="index"/> is held by Body size — itself, or through its whole submesh.</summary>
    private bool PartHeld(int index)
    {
        if (parts == null || index < 0 || index >= parts.Parts.Count) return false;
        var part = parts.Parts[index];
        var holds = RetargetHolds;
        return holds.Contains(part.Label) || (ParentOf(part) is { } parent && holds.Contains(parent.Label));
    }

    /// <summary>A click on the character under Toggle Parts, with a vertex of the triangle it landed on.</summary>
    private void TickClickedOnCharacter(int vertex)
    {
        if (parts == null || vertex < 0 || vertex >= partOfVertex.Length || partOfVertex[vertex] < 0) return;
        Toggle(parts.Parts[partOfVertex[vertex]].Label);
    }

    /// <summary>Whether part <paramref name="index"/> is ticked — itself, or through its whole submesh.</summary>
    private bool PartTicked(int index)
    {
        if (parts == null || index < 0 || index >= parts.Parts.Count) return false;
        var part = parts.Parts[index];
        return ticked.Contains(part.Label) || (ParentOf(part) is { } parent && ticked.Contains(parent.Label));
    }

    /// <summary>A value that changes whenever the ticked set does, for the live tint's cache.</summary>
    private int TickedVersion() => VersionOf(ticked);

    /// <summary>A value that changes whenever <paramref name="labels"/> does, for a live wash's cache.</summary>
    private static int VersionOf(HashSet<string> labels)
    {
        int h = labels.Count;
        foreach (var label in labels) h ^= StringComparer.Ordinal.GetHashCode(label) * 16777619;
        return h;
    }

    // ── move ────────────────────────────────────────────────────────────────

    private readonly Action<int> moveClickedFn;
    private readonly Func<int, bool> moveTickedFn;
    private readonly Func<bool> gizmoCaptureFn;

    /// <summary>The part the Move tool has chosen, or null.</summary>
    private ModelPart? MovePart()
        => movePart == null ? null : parts?.Parts.FirstOrDefault(p => p.Label == movePart);

    /// <summary>Choose the part to move, from the model, the list or the character. Skin and locked parts cannot be.
    /// In polygon mode a part chosen from the list selects all its polygons, to grow or shrink from.</summary>
    private void SelectMovePart(string label)
    {
        if (volume is { Moving: true }) return;
        if (parts?.Parts.FirstOrDefault(p => p.Label == label) is not { } part || IsSkin(part) || IsLocked(part)) return;
        if (movePolygons)
        {
            var polys = Polygons();
            movePolys.Clear();
            for (int t = 0; polys != null && t + 2 < part.Triangles.Length; t += 3)
            {
                var key = PolygonSelection.Key.Of(part.Triangles[t], part.Triangles[t + 1], part.Triangles[t + 2]);
                if (polys.Contains(key)) movePolys.Add(key);
            }
            movePolysVersion++;
            return;
        }
        movePart = label;
        viewport.Recolour();
    }

    private readonly Action<PolygonSelection.Key> polyClickedFn;

    /// <summary>The open model's polygons, built on first use.</summary>
    private PolygonSelection? Polygons() => parts == null ? null : polySelection ??= new PolygonSelection(parts);

    /// <summary>
    /// A click on a polygon, from the model view or the character: it alone is selected, or with Shift held it is added
    /// to the selection, or taken out of it if it was already in.
    /// </summary>
    private void PolygonClicked(PolygonSelection.Key key)
    {
        if (volume is { Moving: true } || Polygons() is not { } polys || !polys.Contains(key)) return;
        if (ImGui.GetIO().KeyShift)
        {
            if (!movePolys.Remove(key)) movePolys.Add(key);
        }
        else
        {
            movePolys.Clear();
            movePolys.Add(key);
        }
        movePolysVersion++;
    }

    /// <summary>Empty the polygon selection; with <paramref name="forgetModel"/>, also the open model's polygons.</summary>
    private void ClearPolygons(bool forgetModel = false)
    {
        if (movePolys.Count > 0) movePolysVersion++;
        movePolys.Clear();
        hoverPoly = null;
        if (forgetModel) polySelection = null;
    }

    /// <summary>
    /// The selected polygons over the model view, and the one under the mouse — the view's own colouring is per part,
    /// too coarse to show a polygon. Also finds <see cref="hoverPoly"/> for this frame's click.
    /// </summary>
    private void DrawPolygonsOverModel()
    {
        hoverPoly = null;
        if (volume == null || Polygons() is not { } polys) return;
        var positions = volume.Positions();

        if (viewport.PointerOverModel && viewport.ScreenRay(ImGui.GetMousePos()) is { } ray)
            hoverPoly = polys.Pick(ray.Origin, ray.Dir, positions);

        var dl = ImGui.GetWindowDrawList();
        void Fill(PolygonSelection.Key key, uint colour)
        {
            if (key.C * 3 + 2 >= positions.Length) return;
            if (viewport.ModelToScreen(PolygonSelection.At(positions, key.A)) is not { } a) return;
            if (viewport.ModelToScreen(PolygonSelection.At(positions, key.B)) is not { } b) return;
            if (viewport.ModelToScreen(PolygonSelection.At(positions, key.C)) is not { } c) return;
            dl.AddTriangleFilled(a, b, c, colour);
        }
        foreach (var key in movePolys) Fill(key, 0x9040A0FFu);                  // ABGR: amber, like a ticked part
        if (hoverPoly is { } hot) Fill(hot, 0x90FFE0B0u);                        // ABGR: pale blue, what a click takes
    }

    private void MoveClickedOnCharacter(int vertex)
    {
        if (parts == null || vertex < 0 || vertex >= partOfVertex.Length || partOfVertex[vertex] < 0) return;
        SelectMovePart(parts.Parts[partOfVertex[vertex]].Label);
    }

    /// <summary>Whether part <paramref name="index"/> is the one being moved — itself, or an island of it.</summary>
    private bool MoveTicked(int index)
    {
        if (parts == null || movePart == null || index < 0 || index >= parts.Parts.Count) return false;
        var part = parts.Parts[index];
        return part.Label == movePart || (ParentOf(part) is { } parent && parent.Label == movePart);
    }

    /// <summary>Drop any drag under way on all three part handles — the model, mod or tool underneath has changed.</summary>
    private void ReleaseHandles()
    {
        moveGizmo.Release();
        rotateGizmo.Release();
        scaleDrag.Release();
    }

    /// <summary>Whether the part labelled <paramref name="label"/> is the chosen one — itself, or an island of it.</summary>
    private bool IsChosenPart(string label)
    {
        if (parts == null || movePart == null) return false;
        for (int i = 0; i < parts.Parts.Count; i++)
            if (parts.Parts[i].Label == label) return MoveTicked(i);
        return false;
    }

    /// <summary>The live tint's cache key for the chosen part — salted, so it never matches a ticked set's.</summary>
    private int MoveVersion() => StringComparer.Ordinal.GetHashCode(movePart ?? "") ^ 0x5BD1E995;

    private IReadOnlySet<string> MoveSelection()
    {
        moveSelectionSet.Clear();
        if (movePart != null) moveSelectionSet.Add(movePart);
        return moveSelectionSet;
    }

    private readonly HashSet<string> moveSelectionSet = new(StringComparer.Ordinal);

    /// <summary>The middle of the chosen part's bounds as it now stands — where the gizmo sits. Null with no part.
    /// In polygon mode, the middle of the selected polygons'.</summary>
    private Vector3? MovePivot()
    {
        if (volume == null) return null;
        IEnumerable<int> corners;
        if (movePolygons)
        {
            if (movePolys.Count == 0) return null;
            corners = PolygonSelection.CornersOf(movePolys);
        }
        else if (MovePart() is { Triangles.Length: > 0 } part) corners = part.Triangles;
        else return null;

        var p = volume.Positions();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int v in corners)
        {
            if (v < 0 || v * 3 + 2 >= p.Length) continue;
            var at = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return min.X <= max.X ? (min + max) * 0.5f : null;
    }

    /// <summary>
    /// Turn the gizmo's drag into a move of the chosen part: the Move tool's <see cref="PumpBrush"/>.
    /// </summary>
    private void PumpMove()
    {
        if (volume == null || !PartTool) return;
        int frame = tool switch
        {
            Tool.Move   => moveGizmo.Frame,
            Tool.Rotate => rotateGizmo.Frame,
            _           => scaleDrag.Frame,
        };
        if (frame != ImGui.GetFrameCount()) return;
        bool started = tool switch
        {
            Tool.Move   => moveGizmo.Started,
            Tool.Rotate => rotateGizmo.Started,
            _           => scaleDrag.Started,
        };
        bool ended = tool switch
        {
            Tool.Move   => moveGizmo.Ended,
            Tool.Rotate => rotateGizmo.Ended,
            _           => scaleDrag.Ended,
        };

        // What the drag carries: the selected polygons, the soft selection measured along the surface; or the part.
        IEnumerable<int>? seeds = movePolygons
            ? movePolys.Count > 0 ? PolygonSelection.CornersOf(movePolys) : null
            : MovePart()?.Triangles;
        if (started && seeds != null)
        {
            if (volume.BeginMove(seeds, moveAdjacent, moveFalloffMm / 1000f, alongSurface: movePolygons) == 0)
            {
                status = Strings.Parts.MoveNothingFree;
                statusIsError = true;
            }
        }

        if (!volume.Moving) return;

        if (tool == Tool.Move) volume.MoveTo(moveGizmo.Offset);
        else volume.TransformTo(tool == Tool.Rotate ? rotateGizmo.Transform : scaleDrag.Transform);
        viewport.PositionOverride = volume.Positions();
        viewport.GeometryChanged();
        previewDirty = !showModelView;

        if (ended) FinishMove();
    }

    /// <summary>End a move drag if one is under way: record it, and save — at once on the character, which shows
    /// nothing until saved, or on the viewer's usual debounce.</summary>
    private void FinishMove()
    {
        if (volume is not { Moving: true }) return;
        volume.EndMove();
        viewport.PositionOverride = volume.Positions();
        viewport.GeometryChanged();
        brushChangedAt = Environment.TickCount64;
        if (!showModelView) SaveBrush();
    }

    private void DrawMove()
    {
        var ps = Strings.Parts;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        if (!showModelView && liveBrush.Problem is { } problem) ImGui.TextColored(ProteusStyle.Warn, problem);
        else ImGui.TextDisabled((tool, showModelView) switch
        {
            (Tool.Rotate, true)  => ps.RotateHelp,
            (Tool.Rotate, false) => ps.RotateLiveHint,
            (Tool.Scale, true)   => ps.ScaleHelp,
            (Tool.Scale, false)  => ps.ScaleLiveHint,
            (_, true)            => ps.MoveHelp,
            _                    => ps.MoveLiveHint,
        });
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        // Whole parts, or single polygons.
        if (ImGui.RadioButton(ps.MoveSelectParts, !movePolygons) && movePolygons && volume is not { Moving: true })
        {
            movePolygons = false;
            ClearPolygons();
            viewport.Recolour();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton(ps.MoveSelectPolygons, movePolygons) && !movePolygons && volume is not { Moving: true })
        {
            movePolygons = true;
            movePart = null;
            viewport.Recolour();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.MoveSelectPolygonsTip);
        ImGui.Spacing();

        if (movePolygons)
        {
            ImGui.PushTextWrapPos(0);
            if (movePolys.Count == 0) ImGui.TextColored(ProteusStyle.Warn, ps.MoveNoPolygons);
            else ImGui.TextUnformatted(string.Format(ps.MovePolygonsFmt, movePolys.Count));
            ImGui.PopTextWrapPos();

            using (ImRaii.Disabled(movePolys.Count == 0 || volume is { Moving: true } || Polygons() == null))
            {
                if (ImGui.SmallButton(ps.MoveGrow)) SetPolygons(Polygons()!.Grow(movePolys));
                ImGui.SameLine();
                if (ImGui.SmallButton(ps.MoveShrink)) SetPolygons(Polygons()!.Shrink(movePolys));
                ImGui.SameLine();
                if (ImGui.SmallButton(ps.MoveClearPolygons)) ClearPolygons();
            }
        }
        else if (MovePart() is { } part)
        {
            ImGui.TextUnformatted(string.Format(ps.MovePartFmt, part.Label));
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(Path.GetFileName(part.Material.TrimStart('/')));
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, ps.MoveNoPart);
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.Checkbox(ps.MoveAdjacent, ref moveAdjacent);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.MoveAdjacentTip);

        using (ImRaii.Disabled(!moveAdjacent))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
            ImGui.SliderFloat(ps.MoveFalloff, ref moveFalloffMm, MinBrushMm, MaxBrushMm,
                              moveFalloffMm < 10f ? "%.1f mm" : "%.0f mm", ImGuiSliderFlags.Logarithmic);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(movePolygons ? ps.MoveFalloffSurfaceTip : ps.MoveFalloffTip);

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(ps.MoveBonesNote);
        ImGui.PopTextWrapPos();

        if (parts != null && Locks.Count > 0)
        {
            ImGui.TextDisabled(string.Format(ps.BrushLockCountFmt, Locks.Count));
            ImGui.SameLine();
            if (ImGui.SmallButton(ps.BrushUnlockAll)) UnlockAll();
        }

        DrawEditActions();
    }

    /// <summary>Replace the polygon selection — a grow or a shrink.</summary>
    private void SetPolygons(HashSet<PolygonSelection.Key> next)
    {
        movePolys.Clear();
        movePolys.UnionWith(next);
        movePolysVersion++;
    }

    private string ViewportKey => modDir + "|" + (modelIndex >= 0 ? models[modelIndex].File : "");

    private string? ModRoot()
    {
        var root = penumbra.GetModDirectory();
        return root == null || modDir == null ? null : Path.Combine(root, modDir);
    }

    // ── the model, and the list beside it ───────────────────────────────────

    /// <summary>
    /// The model on the left, the parts on the right, each driving the other. The list also shows parts hidden
    /// behind others, their materials, and parts the author already switches (a new switch stacks on those).
    /// </summary>
    private void DrawParts(float height)
    {
        var ps = Strings.Parts;
        var model = parts!;

        viewport.Show(ViewportKey, model);
        // Under Body size nothing is being staged for a switch, so nothing shows as selected; the locks show as locks.
        // In polygon mode no whole part is selected; the polygons are drawn over the image instead.
        viewport.Selected = PartTool ? (movePolygons ? NoSelection : MoveSelection())
                          : tool == Tool.Retarget ? NoSelection : ticked;

        // Told every frame rather than on change: the mode also resets when a model is picked.
        viewport.Mode = tool is Tool.Navigate or Tool.Retarget ? PartViewport.ViewportMode.Navigate
                      : PartTool ? PartViewport.ViewportMode.Move
                      : PartViewport.ViewportMode.Brush;
        viewport.GizmoCapture = gizmoCaptureFn;
        viewport.BrushRadius = !PaintTool ? 0f : ActiveRadiusMm / 1000f;
        viewport.VertexScalar = tool == Tool.Wind ? windAt : null;
        viewport.MirrorBrush = mirrorBrush;

        // The share cap is on the image's WIDTH, not the row's height: coupling the row to avail.X flickers as the
        // scrollbar comes and goes.
        // Under a brush a click on the model paints; it only reaches a part with Shift held.
        bool brushing = PaintTool;
        float width = MathF.Min(height * PartViewport.DefaultAspect, ImGui.GetContentRegionAvail().X * 0.55f);
        if (viewport.Draw(model, new Vector2(width, height)) is { } clicked)
        {
            // Body size has nothing to paint, so a plain click on a part holds or frees it — and must never stage a
            // switch, which is what a click means only under Toggle Parts.
            if (PartTool && movePolygons) { if (hoverPoly is { } poly) PolygonClicked(poly); }
            else if (PartTool) SelectMovePart(clicked);
            else if (brushing || tool == Tool.Retarget) ToggleLock(clicked);
            else Toggle(clicked);
        }

        if (PartTool && movePolygons) DrawPolygonsOverModel();

        // Right after the image, so the gizmo is drawn over it, in the same window's draw list.
        if (tool == Tool.Move && MovePivot() is { } pivot)
        {
            moveGizmo.Update(pivot, viewport.ModelToScreen, viewport.ScreenRay, ImGui.GetMousePos(),
                             mouseAllowed: viewport.PointerOverModel, pressed: viewport.Pressed, down: viewport.Held,
                             background: false);
            if (moveGizmo.Capturing) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }
        else if (tool == Tool.Rotate && MovePivot() is { } ringsAt)
        {
            rotateGizmo.Update(ringsAt, viewport.ModelToScreen, viewport.ScreenRay, ImGui.GetMousePos(),
                               mouseAllowed: viewport.PointerOverModel, pressed: viewport.Pressed, down: viewport.Held,
                               background: false);
            if (rotateGizmo.Capturing) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        else if (tool == Tool.Scale && MovePivot() is { } growAbout)
        {
            bool overPart = viewport.PointerOverModel
                            && (movePolygons
                                    ? hoverPoly is { } underPoly && movePolys.Contains(underPoly)
                                    : viewport.Hovered is { } under && IsChosenPart(under));
            scaleDrag.Update(growAbout, viewport.ModelToScreen, ImGui.GetMousePos(), overPart,
                             mouseAllowed: viewport.PointerOverModel, pressed: viewport.Pressed, down: viewport.Held,
                             background: false);
            if (scaleDrag.Capturing) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }

        // A part the author already switches: the new switch stacks. A part with an unreadable tag cannot take one.
        if (tool == Tool.Navigate && viewport.PointerOverModel && viewport.Hovered is { } hot
            && model.Parts.FirstOrDefault(p => p.Label == hot) is { } hovered)
        {
            if (!hovered.Toggleable)          ImGui.SetTooltip(ps.UnreadableTagTip);
            else if (hovered.AuthorSwitched)  ImGui.SetTooltip(ps.StacksWithAuthorTip);
        }

        ImGui.SameLine();
        DrawPartList(model, height);
    }

    private void DrawPartList(ModelParts model, float height)
    {
        var ps = Strings.Parts;

        // A child clips silently, so a horizontal scrollbar admits a row is wider than the list.
        float listWidth = ImGui.GetContentRegionAvail().X;
        using (var group = ImRaii.Child("##partList", new Vector2(listWidth, height), false,
                                        ImGuiWindowFlags.HorizontalScrollbar))
        {
            if (group)
            {
                // Wrapped at the child's own visible width: in a horizontally scrolling window wrap-pos 0 includes last frame's
                // widest row and ratchets wider. Floored, because a negative wrap position means "do not wrap".
                float wrapAt = MathF.Max(
                    listWidth - ImGui.GetStyle().WindowPadding.X * 2f - ImGui.GetStyle().ScrollbarSize,
                    ProteusStyle.S(80f));

                ImGui.PushTextWrapPos(wrapAt);
                ImGui.TextDisabled(tool switch
                {
                    Tool.Navigate => ps.ClickTip,
                    Tool.Move or Tool.Rotate or Tool.Scale => ps.MoveListTip,
                    Tool.Retarget => ps.RetargetLockListTip,
                    _             => ps.BrushLockListTip,
                });
                ImGui.PopTextWrapPos();

                foreach (var (label, count) in model.ShatteredSubmeshes)
                {
                    ImGui.PushTextWrapPos(wrapAt);
                    ImGui.TextDisabled(string.Format(ps.ShatteredFmt, label, count));
                    ImGui.PopTextWrapPos();
                }

                ImGui.Spacing();
                DrawPartRows(model);
            }
        }
    }

    /// <summary>
    /// One row per part, with a submesh's islands folded away behind an expander. Ticked islands always get a row.
    /// </summary>
    private void DrawPartRows(ModelParts model)
    {
        var ps = Strings.Parts;
        string? hoveredRow = null;

        // Under a brush the rows lock parts instead: ticked means the brush moves it. Under Move each row chooses the part to move.
        // Under Body size they are the same locks, and ticked means the refit moves it.
        bool moving = PartTool;
        bool brushing = PaintTool;
        bool refitting = tool == Tool.Retarget;
        bool locking = brushing || refitting;

        // Islands per submesh, so a submesh row can say how many it has and whether to draw them.
        var islands = model.Parts.Where(p => p.Island >= 0)
            .GroupBy(p => (p.Mesh, p.Submesh))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var part in model.Parts)
        {
            bool isIsland = part.Island >= 0;
            var owner = (part.Mesh, part.Submesh);

            // A locked island always has a row under a brush, as a ticked one does for a switch.
            bool listed = moving ? movePart == part.Label
                        : locking ? Locks.Contains(part.Label) : ticked.Contains(part.Label);
            if (isIsland && !expanded.Contains(owner) && !listed) continue;
            if (isIsland) ImGui.Indent(ProteusStyle.S(12f));

            if (moving)
            {
                bool movable = !IsSkin(part) && !IsLocked(part);
                using (ImRaii.Disabled(!movable))
                    if (ImGui.RadioButton($"{part.Label}##mv_{part.Label}", movePart == part.Label))
                        SelectMovePart(part.Label);
                if (!movable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(IsSkin(part) ? ps.MoveSkinTip : ps.MoveLockedTip);
            }
            else if (locking)
            {
                // The brush never moves skin, so its skin rows are fixed at ticked. The refit DOES move skin — the
                // garment's own body mesh has to follow the body — so under Body size a skin row can be held too.
                bool skin = IsSkin(part);
                bool fixedTicked = skin && brushing;
                bool moves = fixedTicked || !IsLocked(part);
                using (ImRaii.Disabled(fixedTicked))
                    if (ImGui.Checkbox($"{part.Label}##l_{part.Label}", ref moves))
                        ToggleLock(part.Label);
                if (fixedTicked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(ps.BrushLockSkinTip);
                else if (refitting && skin && ImGui.IsItemHovered())
                    ImGui.SetTooltip(ps.RetargetLockSkinTip);
            }
            else
            {
                bool on = ticked.Contains(part.Label);
                using (ImRaii.Disabled(!part.Toggleable))
                    if (ImGui.Checkbox($"{part.Label}##p_{part.Label}", ref on))
                        Toggle(part.Label);
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) hoveredRow = part.Label;

            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(ps.RowFmt,
                Path.GetFileName(part.Material.TrimStart('/')), part.TriangleCount));
            if (ImGui.IsItemHovered()) hoveredRow = part.Label;

            // Marked on the row: ticking a part the author already switches means both switches must be on.
            if (part.AuthorSwitched && !locking && !moving)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(ps.AuthorSwitchedTag);
                if (ImGui.IsItemHovered()) hoveredRow = part.Label;
            }

            // The expander sits on the SUBMESH row, because that is the thing being broken up.
            if (!isIsland && islands.TryGetValue(owner, out var count))
            {
                ImGui.SameLine();
                bool open = expanded.Contains(owner);
                if (ImGui.SmallButton(string.Format(open ? ps.HidePiecesFmt : ps.ShowPiecesFmt, count)
                                    + $"##x_{part.Label}"))
                {
                    if (!expanded.Add(owner)) expanded.Remove(owner);
                }
            }

            if (isIsland) ImGui.Unindent(ProteusStyle.S(12f));

            if (hoveredRow == part.Label && !brushing && !moving)
            {
                if (!part.Toggleable)          ImGui.SetTooltip(ps.UnreadableTagTip);
                else if (part.AuthorSwitched)  ImGui.SetTooltip(ps.StacksWithAuthorTip);
            }
        }

        // Only override the viewport's own hover when the cursor is actually over a row; otherwise the
        // model's hover highlight would be cleared by every frame the list is idle.
        if (hoveredRow != null && viewport.Hovered != hoveredRow)
        {
            viewport.Hovered = hoveredRow;
            viewport.Recolour();
        }
    }

    /// <summary>Tick or untick one part, from wherever the click came from.</summary>
    private void Toggle(string label)
    {
        if (parts?.Parts.FirstOrDefault(p => p.Label == label) is not { Toggleable: true }) return;

        if (!ticked.Add(label)) ticked.Remove(label);
        viewport.Recolour();
    }

    // ── the brush ───────────────────────────────────────────────────────────

    /// <summary>
    /// Which tool the drag belongs to. An explicit mode rather than a modifier key: every modifier is already spoken for.
    /// </summary>
    private void DrawToolPicker()
    {
        var ps = Strings.Parts;

        foreach (var (value, icon, label, tip) in new[]
                 {
                     (Tool.Move,     FontAwesomeIcon.ArrowsAlt,         ps.ToolMove,     ps.ToolMoveTip),
                     (Tool.Rotate,   FontAwesomeIcon.SyncAlt,           ps.ToolRotate,   ps.ToolRotateTip),
                     (Tool.Scale,    FontAwesomeIcon.Expand,            ps.ToolScale,    ps.ToolScaleTip),
                     (Tool.Inflate,  FontAwesomeIcon.ExpandArrowsAlt,   ps.ToolInflate,  ps.ToolInflateTip),
                     (Tool.Deflate,  FontAwesomeIcon.CompressArrowsAlt, ps.ToolDeflate,  ps.ToolDeflateTip),
                     (Tool.Relax,    FontAwesomeIcon.Feather,           ps.ToolRelax,    ps.ToolRelaxTip),
                     (Tool.Bridge,   FontAwesomeIcon.Archway,           ps.ToolBridge,   ps.ToolBridgeTip),
                     (Tool.Wind,     FontAwesomeIcon.Wind,              ps.ToolWind,     ps.ToolWindTip),
                     (Tool.Retarget, FontAwesomeIcon.PeopleArrows,      ps.ToolRetarget, ps.ToolRetargetTip),
                     (Tool.Navigate, FontAwesomeIcon.MousePointer,      ps.ToolNavigate, ps.ToolNavigateTip),
                 })
        {
            // IconButtonWithText draws the text itself, so the ###id the label carries is cut off and pushed as an id instead.
            bool clicked;
            var text = label.Split("###")[0];
            using (ImRaii.PushId((int)value))
            using (ProteusStyle.Selected(tool == value))
                clicked = ImGuiComponents.IconButtonWithText(icon, text, FullWidth());

            if (clicked && tool != value)
            {
                // Before leaving the brush: a save rewrites the whole model, so a pending edit would land on top of a switch.
                FinishMove();
                FlushPending();
                // The chosen part carries between Move, Rotate and Scale — they work on the same choice.
                if (!(PartTool && IsPartTool(value)))
                {
                    movePart = null;
                    ClearPolygons();
                }
                tool = value;
                ApplyLocks();   // Body size shows its own holds, every other tool the brush locks
                // Staged parts are a Pick-parts thing; a selection carried into the brush would sit there invisibly.
                ticked.Clear();
                ReleaseHandles();
                viewport.Recolour();
                viewport.GeometryChanged();   // the wind wash comes and goes with the wind tool
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(value is Tool.Navigate or Tool.Retarget || IsPartTool(value)
                                     ? tip
                                     : tip + "\n\n" + ps.BrushLockHint);
        }
        ImGui.Spacing();
    }

    /// <summary>
    /// Turn what the viewport reports into strokes on the geometry. Polled per frame while the button is held; the
    /// expensive passes run on the frame the stroke ends.
    /// </summary>
    private void PumpBrush()
    {
        if (volume == null || !PaintTool) return;
        var surface = Surface;

        if (surface.Painting && surface.Cursor is { } at)
        {
            float radius = ActiveRadiusMm / 1000f;
            int moved = tool switch
            {
                Tool.Relax  => volume.Relax(at, radius, relaxRatePercent / 100f, mirrorBrush),
                Tool.Bridge => volume.Bridge(at, radius, bridgeRatePercent / 100f, surface.ToViewer, mirrorBrush),
                // Ctrl held paints toward none: the eraser, without a second tool or reaching for the slider.
                Tool.Wind   => volume.PaintWind(at, radius, ImGui.GetIO().KeyCtrl ? 0f : windAmountPercent / 100f,
                                                windRatePercent / 100f, mirrorBrush),
                _           => volume.Paint(at, radius,
                                            brushStrengthMm / 1000f * (tool == Tool.Deflate ? -1f : 1f),
                                            surface.ToViewer, mirrorBrush),
            };
            if (moved > 0)
            {
                viewport.PositionOverride = volume.Positions();
                viewport.GeometryChanged();
                // Not for wind: the in-place preview does not show it.
                previewDirty = !showModelView && tool != Tool.Wind;
            }
        }

        if (surface.StrokeEnded)
        {
            volume.EndStroke(bridge: tool == Tool.Bridge, wind: tool == Tool.Wind);
            viewport.PositionOverride = volume.Positions();
            viewport.GeometryChanged();
            brushChangedAt = Environment.TickCount64;

            // On the character, the character IS the preview: save as soon as the stroke is done.
            if (!showModelView) SaveBrush();
        }
    }

    /// <summary>How long after the last change the brush writes itself into the mod.</summary>
    private const long AutosaveMs = 1500;

    /// <summary>
    /// Save the brush once it has been left alone for <see cref="AutosaveMs"/>, then reload the mod and redraw the
    /// character. Debounced from the LAST change; never while the brush is held down.
    /// </summary>
    private void TickAutosave()
    {
        if (brushChangedAt < 0 || Editing) return;
        if (Environment.TickCount64 - brushChangedAt < AutosaveMs) return;
        SaveBrush();
    }

    /// <summary>
    /// Save now if anything is waiting. Called before anything that replaces the model on screen or writes to the
    /// same file, and by the window when the tab stops being drawn.
    /// </summary>
    /// <param name="refreshGame">Reload the mod in Penumbra and redraw the character afterwards. False only
    /// while the plugin is being torn down.</param>
    /// <returns>True when nothing is left waiting. False means a save failed, and the caller must not go on to
    /// write the same file.</returns>
    public bool FlushPending(bool refreshGame = true)
    {
        if (brushChangedAt >= 0) SaveBrush(refreshGame);
        return brushChangedAt < 0;
    }

    private void DrawBrush()
    {
        var ps = Strings.Parts;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        if (showModelView) ImGui.TextDisabled(ps.BrushHelp);
        else if (liveBrush.Problem is { } problem) ImGui.TextColored(ProteusStyle.Warn, problem);
        else ImGui.TextDisabled(ps.LiveHint);
        // Why a stroke on an imported garment does not appear the instant it is painted.
        if (TargetIsContent && !showModelView) ImGui.TextDisabled(ps.ContentHint);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        // A share of the side panel rather than a fixed width, so the label right of each slider fits.
        float w = ImGui.GetContentRegionAvail().X * 0.55f;
        ImGui.SetNextItemWidth(w);
        // Logarithmic, so the small end of the range is not squeezed into the first few pixels.
        if (ImGui.SliderFloat(ps.BrushSize, ref ActiveRadiusMm, MinBrushMm, MaxBrushMm,
                              ActiveRadiusMm < 10f ? "%.1f mm" : "%.0f mm", ImGuiSliderFlags.Logarithmic))
            viewport.Recolour();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushSizeTip + "\n\n" + ps.BrushSizeKeysTip);

        // One Strength slider: a distance for pull and push, a rate for relax. Separate values underneath.
        ImGui.SetNextItemWidth(w);
        // Relax and bridge are both a rate, but each keeps its own value.
        if (tool == Tool.Relax)
        {
            ImGui.SliderFloat(ps.BrushStrength, ref relaxRatePercent, 5f, 100f, "%.0f%%");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushRelaxRateTip);
        }
        else if (tool == Tool.Bridge)
        {
            ImGui.SliderFloat(ps.BrushStrength, ref bridgeRatePercent, 1f, 100f, "%.0f%%");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushBridgeRateTip);
        }
        else if (tool == Tool.Wind)
        {
            ImGui.SliderFloat(ps.BrushWindAmount, ref windAmountPercent, 0f, 100f, "%.0f%%");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushWindAmountTip);
            ImGui.SetNextItemWidth(w);
            ImGui.SliderFloat(ps.BrushWindRate, ref windRatePercent, 1f, 100f, "%.0f%%");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushWindRateTip);

            // What saving will do to the file, said before it happens rather than discovered after.
            ImGui.PushTextWrapPos(0);
            if (volume is { HasWindChannel: false })
                ImGui.TextDisabled(ps.WindAddsChannel);
            if (parts is { FirstColorNotWhite: true })
                ImGui.TextColored(ProteusStyle.Warn, ps.WindFirstColorNotWhite);
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.SliderFloat(ps.BrushStrength, ref brushStrengthMm, 0.01f, 1f, "%.2f mm");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushStrengthTip);
        }

        // A radius barely wider than one vertex moves a spike, not a surface; judged against the mesh's own resolution.
        if (volume is { MeanEdge: > 0f } v && ActiveRadiusMm / 1000f < v.MeanEdge * 1.5f)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn,
                              string.Format(ps.BrushTooSmallFmt, v.MeanEdge * 1000f));
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        if (ImGui.Checkbox(ps.BrushMirror, ref mirrorBrush)) viewport.Recolour();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushMirrorTip);

        // Locks are made on the model, the character or the part list; this only says how many, and lets them all go.
        if (parts != null && Locks.Count > 0)
        {
            ImGui.TextDisabled(string.Format(ps.BrushLockCountFmt, Locks.Count));
            ImGui.SameLine();
            if (ImGui.SmallButton(ps.BrushUnlockAll)) UnlockAll();
        }

        DrawEditActions();
    }

    /// <summary>
    /// What the brushes and Move share under their own controls: how far the edit has gone, carry to other sizes,
    /// undo, start over, save and undo saved changes. One edit underneath both, so one set of actions over it.
    /// </summary>
    private void DrawEditActions()
    {
        var ps = Strings.Parts;
        if (volume is { } vol)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(!vol.Dirty ? ps.BrushUntouched
                : PartTool ? string.Format(ps.MoveMovedFmt, vol.Worst * 1000f)
                : string.Format(ps.BrushMovedFmt, vol.Worst * 1000f, vol.MaxDisplacement * 1000f));

            // Undo and start-over save and redraw at once rather than on the debounce.
            // Offered only where the mod has other sizes of this model to carry the edit to.
            if (OtherSizes().Count > 0)
            {
                bool applying = applySizesTask != null;
                using (ImRaii.Disabled(!vol.Dirty || applying))
                    if (ImGui.Button(ps.BrushApplySizes, FullWidth())) ApplyToOtherSizes();
                ProteusStyle.ReasonTooltip(ps.BrushApplySizesTip);
                if (applying) ImGui.TextDisabled(ps.BrushApplySizesRunning);
            }

            using (ImRaii.Disabled(!vol.CanUndo || vol.Moving))
                if (ImGui.Button(PartTool ? ps.MoveUndo : ps.BrushUndo, FullWidth()))
                { vol.Undo(); AfterBrushEdit(); SaveBrush(); }

            using (ImRaii.Disabled(!vol.Dirty || vol.Moving))
                if (ImGui.Button(ps.BrushReset, FullWidth())) { vol.Reset(); AfterBrushEdit(); SaveBrush(); }

            // Saving happens on its own; on the character the button shows only while a save is still owed.
            if (showModelView || brushChangedAt >= 0)
            {
                ImGui.Spacing();
                using (ImRaii.Disabled(brushChangedAt < 0))
                    if (ImGui.Button(ps.BrushSave, FullWidth())) SaveBrush();
                ProteusStyle.ReasonTooltip(brushChangedAt < 0 ? ps.BrushSaveNothingTip : ps.BrushSaveTip);
            }

            if (brushChangedAt >= 0) ImGui.TextColored(ProteusStyle.Warn, ps.BrushNotSavedYet);

            // Ctrl- or Shift-armed, the house style for anything destructive (see PresetBar's delete).
            if (brushSaved > 0)
            {
                ImGui.Spacing();
                var io = ImGui.GetIO();
                bool armed = io.KeyCtrl || io.KeyShift;
                using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, armed ? 1f : 0.5f))
                using (ImRaii.Disabled(!armed))
                    if (ImGui.Button(ps.BrushRevert, FullWidth())) RevertBrush();
                ProteusStyle.ReasonTooltip(ps.BrushRevertTip + "\n\n" + ps.BrushRevertArmTip);
            }
        }
    }

    private void SaveBrush(bool refreshGame = true)
    {
        if (volume == null || brushBase == null || modelIndex < 0 || modDir == null
            || ModRoot() is not { } root)
        {
            brushChangedAt = -1;   // nothing on screen to save; there is no edit to keep waiting for
            return;
        }

        var rel = models[modelIndex].File;

        // Undone back to nothing on a model never saved: there is nothing in the mod to put right.
        if (!volume.Dirty && !MeshVolumeService.IsPatched(root, rel))
        {
            brushChangedAt = -1;
            return;
        }

        // From the bytes the brush was opened on, not the file on disk. Written even when the edit is now empty, so
        // undoing everything also takes the last save back out of the mod.
        var result = MeshVolumeService.Apply(root, rel, brushBase, volume, writeUntouched: true);
        statusIsError = !result.Ok;
        if (!result.Ok)
        {
            status = result.Message;
            log.Warning("[Proteus] brush: {0}", result.Message);

            // Still waiting: re-armed for another full debounce so a failed save is retried, not every frame.
            brushChangedAt = Environment.TickCount64;
            return;
        }
        brushChangedAt = -1;

        status = string.Format(Strings.Parts.BrushSavedFmt, volume.Worst * 1000f);

        // Wind that could not land, said rather than left to look like the brush missing.
        if (result.WindMeshesRefused > 0)
        {
            status += "\n" + string.Format(Strings.Parts.WindRefusedFmt, result.WindMeshesRefused);
            log.Warning("[Proteus] brush: wind channel could not be added to {0} mesh(es) of {1}",
                        result.WindMeshesRefused, rel);
        }

        // Counted, not silent: a spare left behind puts the author's slots back when that body slider is enabled.
        if (result.UnmappedSpares > 0)
        {
            status += "\n" + string.Format(Strings.Parts.BrushSparesFmt, result.UnmappedSpares);
            log.Warning("[Proteus] brush: {0} shape values could not be carried in {1}",
                        result.UnmappedSpares, rel);
        }
        // Painted wind sways nothing where the garment's materials hold vertex movement at 0, so they are set with it.
        int materialsChanged = 0;
        if (volume.WindEdited && parts != null)
        {
            try
            {
                var mats = MeshVolumeService.ApplyWindMaterials(
                    root, parts.Parts.Where(p => !IsSkin(p)).Select(p => p.Material), redirects);
                materialsChanged = mats.Changed;
                if (mats.Changed > 0) status += "\n" + string.Format(Strings.Parts.WindMaterialsSetFmt, mats.Changed);
                if (mats.Missing > 0) status += "\n" + string.Format(Strings.Parts.WindMaterialsMissingFmt, mats.Missing);
                if (mats.NotInMod > 0) status += "\n" + string.Format(Strings.Parts.WindMaterialsNotInModFmt, mats.NotInMod);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] brush: could not set wind movement on the materials of {0}", rel);
                status += "\n" + string.Format(Strings.Parts.WindMaterialsFailedFmt, ex.Message);
            }
        }

        brushSaved = MeshVolumeService.PatchedCount(root);

        // NOT AfterModChange: that rebuilds the solve on the just-saved bytes and loses the undo history.
        // Reload BEFORE the redraw, or the redraw is handed the bytes Penumbra still has in memory.
        if (!refreshGame) return;

        // On the character, the preview shows the saved state reloaded in place, no redraw; elsewhere, and where Glamourer
        // cannot reload in place, a full redraw. A previewed reload is marked as our own so the compositor does not
        // recomposite. A material rewritten for wind is cached by the game, so wind always redraws.
        bool wind = tool == Tool.Wind;
        // An imported piece is never previewed: the composite rebuilds its shell from the file on reload.
        bool previewed = !showModelView && !preview.UnsupportedFor(TargetIsCustomizePart) && !TargetIsContent
                      && materialsChanged == 0 && !wind;
        if (previewed || (wind && !showModelView)) compositor.ExpectOwnModEdit(modDir);
        penumbra.ReloadModDirectory(modDir);

        if (previewed) previewDirty = true;
        else if (preview.Active) EndLivePreview(refreshGame: true);
        else compositor.RedrawForChangedModel();
    }

    private void RevertBrush()
    {
        if (ModRoot() is not { } root) return;
        brushChangedAt = -1;   // an edit waiting to save must not land on top of the restore
        EndLivePreview(refreshGame: false);   // the redraw below replaces it with the restored file

        var result = MeshVolumeService.Revert(root);
        statusIsError = !result.Ok;
        status = result.Ok
            ? string.Format(Strings.Parts.BrushRevertedFmt, result.FilesWritten)
            : result.Message;

        AfterModChange(root);

        // A recomposite does not redraw gear, so the character would keep the cached brushed model.
        if (result.Ok) compositor.RedrawForChangedModel();
    }

    // ── other sizes ─────────────────────────────────────────────────────────

    /// <summary>
    /// Each other size's bytes as they were the first time the brush was applied to it for this model, so every apply
    /// replaces the last. Written from a worker thread; replaced, never cleared, when another model is opened.
    /// </summary>
    private ConcurrentDictionary<string, byte[]> sizeBases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The apply running on a worker thread, or null; its result is taken up by <see cref="ConsumeApplySizes"/>.</summary>
    private Task<ApplySizesResult>? applySizesTask;

    /// <param name="Root">The mod root the task wrote into.</param>
    /// <param name="ModDir">The mod's Penumbra directory name, to reload.</param>
    private sealed record ApplySizesResult(string Root, string ModDir, int Applied, List<string> Problems, bool WornChanged);

    /// <summary>One size the apply writes: the file, its picker label, and whether the character is wearing it.</summary>
    private readonly record struct SizeTarget(string Rel, string Label, bool Worn);

    /// <summary><see cref="OtherSizes"/>' answer, and the model list and index it was worked out for.</summary>
    private List<int> otherSizesCache = [];
    private List<PenumbraModMeta.Redirect>? otherSizesModels;
    private int otherSizesIndex = -2;

    /// <summary>The mod's other files for the open model's game path, cached per model list and model.</summary>
    private List<int> OtherSizes()
    {
        if (ReferenceEquals(otherSizesModels, models) && otherSizesIndex == modelIndex) return otherSizesCache;
        otherSizesModels = models;
        otherSizesIndex = modelIndex;
        var result = otherSizesCache = [];
        if (modelIndex < 0 || modelIndex >= models.Count) return result;
        var current = models[modelIndex];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MeshVolumeService.Rel(current.File) };
        for (int i = 0; i < models.Count; i++)
            if (string.Equals(models[i].GamePath, current.GamePath, StringComparison.OrdinalIgnoreCase)
                && seen.Add(MeshVolumeService.Rel(models[i].File)))
                result.Add(i);
        return result;
    }

    /// <summary>
    /// Carry the open model's brush edit onto every other size of it in the mod (see <see cref="BrushTransfer"/>),
    /// saving each through the brush's own backup. Runs on a worker thread with the edit snapshotted;
    /// <see cref="ConsumeApplySizes"/> does the framework-thread half.
    /// </summary>
    private void ApplyToOtherSizes()
    {
        if (applySizesTask != null) return;
        if (volume == null || parts == null || modDir == null || ModRoot() is not { } root) return;
        FlushPending();   // the open model is saved first, so every size in the mod agrees

        var worn = WornFiles()
            .Where(w => string.Equals(w.Mod, modDir, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Rel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = OtherSizes()
            .Select(i => new SizeTarget(MeshVolumeService.Rel(models[i].File), modelLabels[i],
                                        worn.Contains(MeshVolumeService.Rel(models[i].File))))
            .ToList();
        if (targets.Count == 0) return;

        var edit = BrushTransfer.Edit.From(volume, parts.Positions.Length / 3);
        var source = parts;
        var modRedirects = redirects;
        var bases = sizeBases;
        var dir = modDir;
        var ps = Strings.Parts;

        applySizesTask = Task.Run(() =>
        {
            int applied = 0;
            bool wornChanged = false;
            var problems = new List<string>();
            foreach (var t in targets)
            {
                try
                {
                    var bytes = bases.GetOrAdd(t.Rel, rel => File.ReadAllBytes(
                        Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));
                    if (ModelPartReader.Read(bytes) is not { } targetParts)
                    {
                        problems.Add(string.Format(ps.BrushApplySizesProblemFmt, t.Label, ps.Unreadable));
                        continue;
                    }

                    var target = BrushTransfer.Transfer(edit, source, targetParts);
                    var result = MeshVolumeService.Apply(root, t.Rel, bytes, target, writeUntouched: true);
                    if (!result.Ok)
                    {
                        problems.Add(string.Format(ps.BrushApplySizesProblemFmt, t.Label, result.Message));
                        continue;
                    }
                    if (target.WindEdited)
                        MeshVolumeService.ApplyWindMaterials(
                            root, targetParts.Parts.Where(p => !IsSkin(p)).Select(p => p.Material), modRedirects);
                    applied++;
                    wornChanged |= t.Worn;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[Proteus] brush: could not apply to {0}", t.Rel);
                    problems.Add(string.Format(ps.BrushApplySizesProblemFmt, t.Label, ex.Message));
                }
            }
            return new ApplySizesResult(root, dir, applied, problems, wornChanged);
        });
    }

    /// <summary>
    /// Take up a finished <see cref="ApplyToOtherSizes"/> on the framework thread: say what happened, and tell
    /// Penumbra and the game. Called every frame the tab draws.
    /// </summary>
    private void ConsumeApplySizes()
    {
        if (applySizesTask is not { IsCompleted: true } task) return;
        applySizesTask = null;

        if (task.IsFaulted || task.IsCanceled)
        {
            log.Warning(task.Exception, "[Proteus] brush: applying to other sizes failed");
            statusIsError = true;
            status = task.Exception?.GetBaseException().Message ?? "";
            return;
        }

        var r = task.Result;
        statusIsError = r.Applied == 0 && r.Problems.Count > 0;
        status = string.Join("\n", new[] { string.Format(Strings.Parts.BrushAppliedSizesFmt, r.Applied) }.Concat(r.Problems));
        if (string.Equals(ModRoot(), r.Root, StringComparison.OrdinalIgnoreCase))
            brushSaved = MeshVolumeService.PatchedCount(r.Root);

        if (r.Applied == 0) return;
        // Sizes nobody is wearing need only Penumbra told; one the character IS wearing has to be redrawn to show.
        if (!r.WornChanged) compositor.ExpectOwnModEdit(r.ModDir);
        penumbra.ReloadModDirectory(r.ModDir);
        if (!r.WornChanged) return;
        if (preview.Active) EndLivePreview(refreshGame: true);
        else compositor.RedrawForChangedModel();
    }

    private void AfterBrushEdit()
    {
        if (volume == null) return;
        viewport.PositionOverride = volume.Positions();
        viewport.GeometryChanged();
        brushChangedAt = Environment.TickCount64;
    }

    // ── body retarget ───────────────────────────────────────────────────────

    /// <summary>
    /// Hand the Body size tool everything it needs for this frame. The panel reaches nothing of this class directly:
    /// what it may do arrives as a handful of callbacks, so the tab keeps sole ownership of the preview, the status
    /// line and the pending save.
    /// </summary>
    private void DrawRetarget()
    {
        var ps = Strings.Parts;
        if (ModRoot() is not { } root || modDir == null || parts == null || brushBase == null
            || modelIndex < 0 || modelIndex >= models.Count)
        {
            ImGui.TextWrapped(ps.RetargetNoModel);
            return;
        }

        retarget.Draw(new BodyRetargetPanel.RetargetContext(
            root, modDir, MeshVolumeService.Rel(models[modelIndex].File), models[modelIndex].GamePath,
            modelLabels[modelIndex], parts, brushBase, redirects,
            FlushPending: () => FlushPending(),
            PushPreview: PushRetargetPreview,
            EndPreview: () => EndLivePreview(refreshGame: true),
            SetStatus: (text, error) => { status = text; statusIsError = error; },
            AfterModChange: () =>
            {
                compositor.ExpectOwnModEdit(modDir);
                penumbra.ReloadModDirectory(modDir);
                compositor.RedrawForChangedModel();
                // The save added a model (or an undo took one away): list it, so the new size can be opened here.
                // Next frame, not now: this runs inside the side panel, and the model view drawn after it in this same
                // frame must not find the open model closed under it.
                refreshModelsPending = true;
            },
            Held: RetargetHolds));
    }

    /// <summary>
    /// Put a refitted model on the character, through the same temporary redirect the brush previews with.
    /// <para/>
    /// The bytes are complete rather than an edit to tick towards, so this pushes once and does not set
    /// <see cref="previewDirty"/>: there is no stroke behind it to throttle.
    /// </summary>
    /// <returns>False only when the preview is busy and the push should be tried again next frame. A model that can
    /// never be previewed here answers true, so the caller stops retrying.</returns>
    private bool PushRetargetPreview(byte[] bytes)
    {
        if (modelIndex < 0 || modelIndex >= models.Count) return true;
        if (preview.UnsupportedFor(TargetIsCustomizePart) || TargetIsContent) return true;

        var file = models[modelIndex].File.Replace('\\', '/');
        var gamePaths = redirects
            .Where(r => string.Equals(r.File.Replace('\\', '/'), file, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GamePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gamePaths.Count == 0) return true;

        return preview.Push(bytes, gamePaths, TargetIsCustomizePart);
    }

    // ── staging ─────────────────────────────────────────────────────────────

    private void DrawStaging()
    {
        var ps = Strings.Parts;

        // An imported piece takes switches too: it is worn on a host item, so Proteus hides its parts itself from the
        // sidecar, which MeshToggleService.SyncContentAttributes keeps in step with the group written here.
        int free = freeLetters;

        ImGui.TextDisabled(string.Format(ps.SelectedFmt, ticked.Count));

        ImGui.Spacing();
        int left = free - pending.Count;
        if (left <= 0)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, ps.NoBudget);
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.TextDisabled(string.Format(ps.BudgetFmt, left));
        }

        // The Add button on its own line: in the side panel a name box and a button side by side do not fit.
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        ImGui.InputText(ps.ToggleName, ref toggleName, 64);

        bool canAdd = left > 0 && ticked.Count > 0 && !string.IsNullOrWhiteSpace(toggleName);
        using (ImRaii.Disabled(!canAdd))
            if (ImGui.Button(ps.AddBtn, FullWidth()))
            {
                pending.Add((toggleName.Trim(), [.. ticked]));
                ticked.Clear();
                toggleName = string.Empty;
                viewport.Recolour();
            }
        if (!canAdd && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ticked.Count == 0 ? ps.NeedParts : ps.NeedName);

        if (pending.Count == 0) return;

        ImGui.Spacing();
        ProteusStyle.SectionHeader(ps.PendingHeader);
        for (int i = 0; i < pending.Count; i++)
        {
            var (name, list) = pending[i];
            // Wrapped, since a switch's part list runs long and the side panel is narrow.
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(string.Format(ps.PendingFmt, name, string.Join(", ", list)));
            ImGui.PopTextWrapPos();
            if (ImGui.Button($"{ps.RemoveBtn}##rm{i}", FullWidth())) { pending.RemoveAt(i); break; }
        }

        ImGui.Spacing();
        if (ImGui.Button(ps.WriteBtn, FullWidth())) Commit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.WriteTip);
        ImGui.TextColored(ProteusStyle.Warn, ps.NotWrittenYet);
    }

    /// <summary>What Proteus has already put into this mod, and the way back out.</summary>
    private void DrawExisting()
    {
        var ps = Strings.Parts;
        if (existing is not { Items.Count: > 0 } record) return;

        ProteusStyle.SectionHeader(ps.ExistingHeader);
        // Listed per item, because that is how they are grouped in Penumbra.
        foreach (var item in record.Items)
            ImGui.TextDisabled($"{item.GroupName}: {string.Join(", ", item.Toggles.Keys)}");
        if (ImGui.Button(ps.RevertBtn)) Revert();
        ImGui.Separator();
    }

    private void DrawStatus()
    {
        if (status == null) return;
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(statusIsError ? ProteusStyle.Bad : ProteusStyle.Ok, status);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
    }

    // ── writing ─────────────────────────────────────────────────────────────

    private void Commit()
    {
        // A brush save rewrites the whole model from its opening bytes and would erase the switch, so save first;
        // if that fails the switch is not written.
        if (!FlushPending()) return;
        var ps = Strings.Parts;
        if (parts == null || modelIndex < 0 || ModRoot() is not { } root) return;

        var byLabel = parts.Parts.ToDictionary(p => p.Label, StringComparer.Ordinal);
        var plans = pending
            .Select(t => new MeshToggleService.Plan(
                t.Name, t.Parts.Where(byLabel.ContainsKey).Select(l => byLabel[l]).ToList()))
            .Where(p => p.Parts.Count > 0)
            .ToList();

        // An imported pack's pieces publish nothing, so its other copies at this game path (another size, say) are
        // offered as siblings from the sidecar rows.
        var siblings = redirects.Concat(models.Where(m => contentFiles.Contains(m.File))).ToList();
        var result = MeshToggleService.Write(
            root, models[modelIndex], parts, plans, siblings,
            gamePath => textureLoader.LoadRawFile(null, gamePath));

        statusIsError = !result.Ok;
        if (!result.Ok)
        {
            status = result.Message;
            log.Warning("[Proteus] parts: {0}", result.Message);
            return;
        }

        status = string.Format(ps.WrittenFmt, plans.Count, result.GroupName);
        if (result.Skipped.Count > 0) status += "\n" + string.Format(ps.SkippedFmt, result.Skipped.Count);

        pending.Clear();
        ticked.Clear();
        AfterModChange(root);
    }

    private void Revert()
    {
        if (ModRoot() is not { } root) return;

        // Same reason as Commit: a pending brush edit flushed after the restore would put the switches back.
        // If the save fails, nothing is restored.
        if (!FlushPending()) return;

        var result = MeshToggleService.Revert(root);
        statusIsError = !result.Ok;
        status = result.Ok ? string.Format(Strings.Parts.RevertedFmt, result.FilesPatched) : result.Message;

        pending.Clear();
        ticked.Clear();
        AfterModChange(root);
    }

    /// <summary>
    /// Re-read everything the mod's files say, and make Penumbra do the same; a split renumbers parts.
    /// </summary>
    private void AfterModChange(string root)
    {
        existing = MeshToggleService.ReadRecord(root);
        brushSaved = MeshVolumeService.PatchedCount(root);
        viewport.Clear();   // so it rebuilds its pickable set against the edited model
        if (modelIndex >= 0) SelectModel(modelIndex);

        if (modDir != null) penumbra.ReloadModDirectory(modDir);
        compositor.TriggerRecomposite("parts-written");
    }

}
