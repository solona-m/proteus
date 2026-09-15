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
/// The Parts tab: pick geometry out of a mod's model and put it behind an on/off switch.
/// <para/>
/// It exists because a great many mods ship geometry nobody can turn off — a bow, a collar, a strap welded
/// into an always-on mesh — and Penumbra can only offer what the author built. Rather than host a stripped
/// copy, Proteus edits the mod so it carries a real switch of its own: an attribute on the geometry and an
/// IMC group over it, which is the mechanism the author would have used. The switch then works with Proteus
/// turned off, which is both the point and the acceptance test.
/// <para/>
/// Its own class rather than another method on <c>StatusWindow</c>, and its own tab rather than a button on
/// the Mods list, for the same reason: that list is Proteus's sidecar mods, and this works on ANY installed
/// mod — most of them will never have heard of Proteus.
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

    private string? modDir;

    /// <summary>Every redirect the mod publishes. Kept UNFILTERED alongside <see cref="models"/> because the
    /// writer needs the material paths to read the item's variant off — filtering to models before handing
    /// the list over left it with nothing to find.</summary>
    private List<PenumbraModMeta.Redirect> redirects = [];

    /// <summary>Just the models, for the picker.</summary>
    private List<PenumbraModMeta.Redirect> models = [];

    /// <summary>
    /// What the picker shows for each entry of <see cref="models"/>, resolved once when the list is built.
    /// <para/>
    /// Not per frame: naming a model runs <c>ContentSlot.Parse</c>, which is a compiled regex, and builds a
    /// string — and the combo asks for the current label on every frame whether it is open or not. Same
    /// reasoning as <see cref="Strings"/> resolving its text once per language.
    /// </summary>
    private List<string> modelLabels = [];
    private int modelIndex = -1;

    private ModelParts? parts;

    /// <summary>
    /// How many switch letters this model has left, resolved when the model is read rather than per frame.
    /// <para/>
    /// It only changes when the model does, and <c>FreeLetters</c> allocates a set and a list every call —
    /// the same reason <see cref="Strings"/> resolves its text once per language instead of once per frame.
    /// </summary>
    private int freeLetters;
    private bool modelUnreadable;

    /// <summary>The picked mod is in Penumbra's pre-v4 layout, which Proteus reads but will not write —
    /// see <see cref="PenumbraModMeta.IsLegacyFolder"/>. Nothing below the mod picker is drawn for one.</summary>
    private bool modIsLegacy;

    private readonly HashSet<string> ticked = new(StringComparer.Ordinal);
    /// <summary>Submeshes whose islands are listed out. See <see cref="DrawPartRows"/>.</summary>
    private readonly HashSet<(int Mesh, int Submesh)> expanded = [];

    /// <summary>
    /// Parts locked against the brush, by label, per model (keyed like <see cref="ViewportKey"/>). A submesh's
    /// label covers all its islands. Kept for the session across tools, saves and switching models, since a lock
    /// is about the garment — the belt on these trousers — and never written into the mod.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> lockedParts = [];

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
    }

    /// <summary>Pull out to start with: clearing a body poking through a garment is what the tab is opened for most.</summary>
    private Tool tool = Tool.Inflate;

    /// <summary>The handle the Move tool drags, shared by the model view and the character.</summary>
    private readonly TranslateGizmo moveGizmo = new();

    /// <summary>The part the Move tool has chosen, by label; null for none. Cleared with the model or the tool.</summary>
    private string? movePart;

    /// <summary>
    /// Move carries the cloth joined to the part along, fading out over <see cref="moveFalloffMm"/> — on by default,
    /// because a sleeve moved without the shoulder it is sewn to stretches one row of triangles into a sliver.
    /// </summary>
    private bool moveAdjacent = true;

    /// <summary>How far along the surface the joined cloth follows a move, in millimetres.</summary>
    private float moveFalloffMm = 50f;

    /// <summary>
    /// The editable geometry of the model on screen, or null before one is picked. Rebuilt only when the
    /// model changes — the weld and the boundary scan depend on topology, which a stroke never alters.
    /// </summary>
    private MeshVolumeSolve? volume;

    /// <summary>Brush radius and per-dab strength, both in millimetres because that is how the problem is
    /// described: "the hip pokes through by about a millimetre".</summary>
    private float brushRadiusMm = 200f, brushStrengthMm = 0.4f;

    /// <summary>
    /// The bridge brush's own size, smaller than the others'. A bridge spans the hollow under the brush, so a
    /// brush far wider than the crack reaches past it onto the curves either side and bridges between those
    /// too; starting it near the width of the thing it is for keeps the span where it was meant.
    /// </summary>
    private float bridgeRadiusMm = 70f;

    /// <summary>The size the current tool paints with — the bridge keeps its own, see above.</summary>
    private ref float ActiveRadiusMm
        => ref tool == Tool.Bridge ? ref bridgeRadiusMm
             : ref (tool == Tool.Wind ? ref windRadiusMm : ref brushRadiusMm);

    /// <summary>The open model's wind lookup for the viewer's wash, made once per model rather than per frame.</summary>
    private Func<int, float>? windAt;

    /// <summary>The wind brush's own size — see <see cref="windAmountPercent"/>.</summary>
    private float windRadiusMm = 15f;

    /// <summary>
    /// The wind being painted, as a percentage of full sway: each dab moves the surface toward it. Its own field,
    /// like each brush's strength, so switching tools never reinterprets another brush's number.
    /// </summary>
    private float windAmountPercent = 5f;

    /// <summary>How much of the way to <see cref="windAmountPercent"/> each moment of painting goes.</summary>
    private float windRatePercent = 15f;

    /// <summary>
    /// The relax brush's strength, as a percentage — how far each moment of painting moves the surface toward
    /// its neighbours. Its own field rather than <see cref="brushStrengthMm"/>, which is a distance: switching
    /// tools must not reinterpret 0.05 mm as 0.05 %.
    /// </summary>
    private float relaxRatePercent = 30f;

    /// <summary>
    /// The bridge brush's rate, as a percentage — how much of the remaining gap each moment of painting
    /// closes. Its own field, and far lower than relax's: a bridge closes the gap it measures each dab, so a
    /// high rate fills a crack before there is time to see how far it should go.
    /// </summary>
    private float bridgeRatePercent = 5f;

    /// <summary>How many models in this mod the brush has already written, so the way back can be offered
    /// only when there is something to go back from.</summary>
    private int brushSaved;

    /// <summary>
    /// The model's bytes as they were when the brush was opened on it. Every save is these plus the whole
    /// edit so far — see <see cref="MeshVolumeService.Apply"/> — so saving repeatedly never stacks.
    /// </summary>
    private byte[]? brushBase;

    /// <summary>When the brush last changed something not yet saved, as a tick count; -1 when nothing waits.</summary>
    private long brushChangedAt = -1;

    /// <summary>
    /// Paint in the model viewer in this window rather than on the character in the game world. Off by default:
    /// the character is what the edit is for, and painting it directly shows the result in its own lighting
    /// and pose. The viewer stays for anything the character cannot show — a part hidden under another, a
    /// garment not currently worn.
    /// </summary>
    private bool showModelView;

    /// <summary>Where strokes come from this frame.</summary>
    private IBrushSurface Surface => showModelView ? viewport : liveBrush;

    /// <summary>Puts the edit on the character while painting on it — see <see cref="LiveBrushPreview"/>.</summary>
    private readonly LiveBrushPreview preview;

    /// <summary>The solve has changed since the character last showed it.</summary>
    private bool previewDirty;

    private long lastPreviewAt;

    /// <summary>
    /// Least time between previews while the brush is down. Each is a model rebuild, a file write and a gear
    /// reload; faster than this the reloads queue behind one another and the character lags further behind
    /// the brush rather than closer.
    /// </summary>
    private const long PreviewIntervalMs = 150;

    public PartsPanel(
        PenumbraBridge penumbra, CompositorService compositor, PartViewport viewport, LiveBrush liveBrush,
        TextureLoader textureLoader, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.viewport = viewport;
        this.liveBrush = liveBrush;
        preview = new LiveBrushPreview(penumbra, compositor, log);
        LiveBrushPreview.CleanUp();
        partOfVertexFn = PartOfVertex;
        lockClickedFn = LockClickedOnCharacter;
        tickClickedFn = TickClickedOnCharacter;
        partTickedFn = PartTicked;
        moveClickedFn = MoveClickedOnCharacter;
        moveTickedFn = MoveTicked;
        gizmoCaptureFn = () => moveGizmo.Capturing;
        this.textureLoader = textureLoader;
        this.log = log;
    }


    /// <summary>Drop the mod list so the next frame re-reads it — wired to the window's Refresh.</summary>
    public void Refresh() => mods = null;

    /// <summary>
    /// Whether the last <see cref="Draw"/> drew the model viewer. The window reads the moment this turns on
    /// to grow itself, since the viewer is what the room is for and the size that suits a mod picker is far
    /// too small to paint on.
    /// </summary>
    public bool ShowingModel { get; private set; }

    /// <param name="fillHeight">Whether the model row may take all the height that is left. True only while
    /// the window is user-resizable, which it is only on this tab — see the remarks on the computation.</param>
    /// <param name="reserveBelow">Height the window itself still needs under the tab content (its footer).</param>
    public void Draw(bool fillHeight, float reserveBelow)
    {
        var ps = Strings.Parts;
        ShowingModel = false;
        TickAutosave();
        ConsumeApplySizes();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(ps.Intro);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (modDir == null && !autoPicked)
        {
            autoPicked = true;
            AutoPickWorn();
        }

        DrawLivePick();
        DrawModPicker();
        if (modDir == null) return;

        if (modIsLegacy)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, ps.LegacyMod);
            ImGui.PopTextWrapPos();

            // Undo still offered. An older Proteus DID write into folders like this one, so a mod here may
            // carry switches of ours — and hiding the button would leave the one person who needs it most
            // with no way out. The models come back either way; only removing the option group can fail,
            // and it says so.
            DrawExisting();
            DrawStatus();
            return;
        }

        DrawExisting();
        DrawStatus();
        DrawModelPicker();
        if (modelIndex < 0) return;

        if (modelUnreadable)
        {
            ImGui.TextColored(ProteusStyle.Warn, ps.Unreadable);
            return;
        }
        if (parts == null) return;

        ImGui.Separator();

        // ── why the fill branch is safe, and only here ──
        // It reads back a height the layout itself produces, which is the shape of a feedback loop. It is
        // stable ONLY because this branch runs while the window is NOT auto-resizing: avail.Y is then a
        // function of the size the user dragged to and nothing else, so a taller row cannot make a taller
        // window. Under AlwaysAutoResize — every other tab, and this one for the frame either side of a tab
        // switch — the same expression grows without bound until it hits MaximumSize, which is why the fixed
        // height has to remain the path for every non-fill frame rather than being replaced by it.
        //
        // Nothing is drawn below the row any more — the controls moved into the side panel — so there is no
        // tail to measure and leave room for: the row takes all the height the window has.
        // Not a fixed height when the window is fitting itself: the side panel's own content, measured last frame,
        // so the fit leaves every control on screen instead of behind the panel's scrollbar.
        // Plus the same 4 px the fill branch below takes off, so the row the window was fitted to is exactly the
        // row fill mode hands back — and the panel is not left a sliver short, with a scrollbar, once it does.
        float height = MathF.Max(ProteusStyle.S(360f), sidePanelContent + ProteusStyle.S(4f));
        if (fillHeight)
            height = MathF.Max(ImGui.GetContentRegionAvail().Y - reserveBelow - ProteusStyle.S(4f),
                               ProteusStyle.S(200f));

        // The window grows for the viewer only; controls alone fit the size it already is.
        ShowingModel = showModelView;
        HandleUndoShortcut();
        HandleBrushSizeKeys();

        // The tools and their controls down the LEFT, beside the model rather than under it. Under it, every
        // slider and button cost the model its height, and a model you paint on wants all the height there is.
        // One set of controls or the other, never both: staging a switch and brushing geometry are different
        // jobs, and showing both invites writing a switch while thinking about a brush.
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
                if (tool == Tool.Navigate) DrawStaging();
                else if (tool == Tool.Move) DrawMove();
                else DrawBrush();

                // Window-local, so it already counts any scroll; plus the panel's bottom padding and border.
                sidePanelContent = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y + 2f;
            }
        }

        if (showModelView)
        {
            ImGui.SameLine();
            DrawParts(height);
        }
        else
        {
            // Without the viewer, picking parts for a switch is done from the list alone — and under a brush the
            // same list locks parts against it.
            ImGui.SameLine();
            DrawPartList(parts, height);

            // Under Toggle Parts too, where nothing paints: a click on the open garment ticks the part under it, as a
            // click on the model view does — and a click on any other worn garment still opens that one.
            bool pickParts = tool == Tool.Navigate;
            bool moving = tool == Tool.Move;
            if (volume != null && brushBase != null && ModRoot() is { } root)
                liveBrush.ArmBrush(Path.Combine(root, models[modelIndex].File.Replace('/', Path.DirectorySeparatorChar)),
                                   brushBase, volume, ActiveRadiusMm / 1000f, showWind: tool == Tool.Wind,
                                   mirror: mirrorBrush, partOf: partOfVertexFn,
                                   lockClicked: moving ? moveClickedFn : pickParts ? tickClickedFn : lockClickedFn,
                                   pickParts: pickParts,
                                   partTicked: moving ? moveTickedFn : partTickedFn,
                                   tickedVersion: moving ? MoveVersion() : TickedVersion(),
                                   moveGizmo: moving ? moveGizmo : null,
                                   movePivot: moving ? MovePivot() : null);
        }

        PumpMove();
        PumpBrush();
        TickLivePreview();
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

        // This kind of part turned out not to reload in place (found out after a push, once the save had already
        // counted on it): show the finished stroke the old way, once the brush is up — preview taken down first,
        // so the redraw loads the saved file and not the last preview that did not land.
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
            // The save will say the same thing properly; the preview just stops trying.
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

    /// <summary>
    /// Take the preview off the character, so it draws the mod's own (saved) file again. Before anything that
    /// changes which model or which file the preview stands in for.
    /// </summary>
    private void EndLivePreview(bool refreshGame)
    {
        previewDirty = false;
        preview.End(redraw: refreshGame);
    }

    /// <summary>
    /// The tab is being left, closed or torn down: save anything waiting and take the preview down.
    /// </summary>
    /// <param name="refreshGame">False on teardown, when nothing should be poked beyond landing the file.</param>
    public void Leave(bool refreshGame = true)
    {
        FinishMove();   // a drag cut off by leaving still counts, and still undoes
        FlushPending(refreshGame);
        EndLivePreview(refreshGame);
        autoPicked = false;   // the next visit may find different gear on
    }

    /// <summary>
    /// Choosing a garment by clicking it on the character — on while painting on the character with Toggle Parts
    /// selected or nothing open to brush yet, with a line above the mod picker saying so.
    /// <para/>
    /// Not behind a button, because it replaces the pickers: finding the right mod among hundreds, then the right
    /// model among its sizes, is the slowest part of fixing a clip, and the character already knows the answer.
    /// But OFF under a brush with a model open: a stroke that starts a hair off the edge of the garment, or a click
    /// meant for the garment that lands on the one beside it, used to swap the model out from under the brush.
    /// Switching garments is a Toggle Parts job, or the pickers'.
    /// </summary>
    private void DrawLivePick()
    {
        if (showModelView || penumbra.GetModDirectory() is not { } modsRoot) return;
        if (tool != Tool.Navigate && volume != null) return;
        liveBrush.ArmPick(modsRoot, OnLivePicked);
        ImGui.TextDisabled(Strings.Parts.LivePickTip);
        ImGui.Spacing();
    }

    /// <summary>
    /// Open the model of the chosen mod that the character is wearing — the row the model picker shows green —
    /// so picking a mod lands on the size actually on screen instead of an empty model box. When several of the
    /// mod's models are worn, the same slot order as <see cref="AutoPickWorn"/>: body, legs, hands, feet, head.
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
    /// Open the garment most likely to be the one needing a fix, so the tab arrives ready to paint: the worn
    /// chest piece if it comes from a mod, else the legs, then hands, feet and head — and the exact file the
    /// character is wearing, which for a mod offering sizes is the size selected in Penumbra.
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

    /// <summary>How tall the side panel's controls came out last frame, scaled — the height it asks for while the
    /// window fits itself to the tab.</summary>
    private float sidePanelContent;

    /// <summary>How tall the tab's controls need to be, so the window can fit itself again when they grow.</summary>
    public float ControlsHeight => sidePanelContent;

    /// <summary>
    /// Ctrl+Z takes back the last brush stroke — the same as the Undo stroke button, saving and redrawing at
    /// once.
    /// <para/>
    /// Only while this window has focus, so Ctrl+Z pressed in the game or in another plugin does nothing here;
    /// never while a text box has the keyboard, where Ctrl+Z belongs to the text; and never mid-stroke, where
    /// it would undo the stroke still being painted out from under the brush. Nor while picking parts, where
    /// no brush controls are showing and Ctrl+Z would read as taking back a tick, not rewriting the model.
    /// </summary>
    private void HandleUndoShortcut()
    {
        bool pressed = undoKey.Poll();   // every frame — see HeldKey
        if (!pressed || tool == Tool.Navigate || volume is not { CanUndo: true } vol || Editing) return;
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
    /// Whether a key pressed now is meant for this tab: the game is the foreground window, no text box has the
    /// keyboard, and this window is focused or under the mouse — or, painting on the character, which clicks into
    /// the game world and so takes focus from every window, no window is focused at all.
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
    /// <c>[</c> and <c>]</c> shrink and grow the current tool's brush — the Photoshop and Krita keys. By a PROPORTION
    /// rather than a fixed step, so a press means the same at 3 mm as at 250 mm; Shift for fine steps. Held, they
    /// repeat. Gated like <see cref="HandleUndoShortcut"/> — see <see cref="ShortcutsHaveTheKeyboard"/>.
    /// </summary>
    private void HandleBrushSizeKeys()
    {
        bool grow = growKey.Poll(), shrink = shrinkKey.Poll();   // every frame — see HeldKey
        if (!grow && !shrink) return;
        if (tool is Tool.Navigate or Tool.Move || volume == null) return;
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
        // Proteus's own output mod is left out. It is rebuilt from scratch on every composite, so a switch or a
        // brush edit written into it lasts until the next one — and because the character is always drawing
        // it, it would otherwise head the worn list above the garments someone actually came here to fix.
        mods ??= (penumbra.GetAllMods() ?? [])
            .Where(m => !string.Equals(m.Key, SidecarDiscoveryService.ManagedModDir,
                                       StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.Key, m => m.Value);

        var width = ProteusStyle.S(340f);
        ImGui.SetNextItemWidth(width);

        // The height cap has to be explicit: BeginCombo applies its own row limit ONLY when the caller
        // supplied no size constraint, so passing a width silently disables it and the popup would grow one
        // row per mod — and this list is EVERY mod Penumbra knows, which is routinely several hundred.
        var popupMaxH = ImGui.GetTextLineHeightWithSpacing() * 18 + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width * 2.2f, popupMaxH));

        var current = modDir != null && mods.TryGetValue(modDir, out var name) ? name : ps.PickMod;
        if (!ImGui.BeginCombo(ps.Mod + "##partsMod", current)) return;

        // Fresh filter each open, with the caret already in the box so the list can just be typed at.
        // SetKeyboardFocusHere targets the NEXT item submitted, so it has to sit immediately before it.
        bool appearing = ImGui.IsWindowAppearing();
        if (appearing)
        {
            modFilter = "";
            // Asked once per open, not per frame: it is an IPC round trip over every resource the character
            // has loaded, and what is worn does not change while someone is reading a list.
            equippedMods = EquippedModDirectories();
        }
        ImGui.SetNextItemWidth(-1);
        if (appearing) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##partsFilter", Strings.Export.FilterHint, ref modFilter, 64);
        ImGui.Separator();

        // Worn mods first, then everything else, each alphabetical. A garment that clips is almost always one
        // being worn, and finding it among several hundred installed mods is the slowest part of fixing it.
        int shown = 0;
        bool anyEquippedShown = false, separated = false;
        foreach (var (dir, label) in mods
                     .OrderBy(m => equippedMods.Contains(m.Key) ? 0 : 1)
                     .ThenBy(m => m.Value, StringComparer.OrdinalIgnoreCase))
        {
            // Folder as well as name. The two routinely differ — Penumbra's folder is a sanitised form of
            // the name, and either can be renamed — so filtering on the label alone hides mods someone is
            // searching for by folder.
            if (modFilter.Length > 0
                && label?.Contains(modFilter, StringComparison.OrdinalIgnoreCase) != true
                && dir?.Contains(modFilter, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            bool worn = equippedMods.Contains(dir);
            if (worn) anyEquippedShown = true;
            else if (anyEquippedShown && !separated) { ImGui.Separator(); separated = true; }

            shown++;
            // ##dir: two mods can share a display name, and duplicate ImGui ids would route the click to
            // the wrong row.
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
    /// Which installed mods the character is wearing, by folder name: every mod that a currently loaded
    /// model file is being read from.
    /// <para/>
    /// Models, not textures or materials, because this tab edits models — a mod that only recolours the gear
    /// being worn has nothing here to pick. Empty rather than null when Penumbra cannot answer, so the list
    /// simply falls back to alphabetical.
    /// </summary>
    private HashSet<string> EquippedModDirectories()
        => WornFiles().Select(w => w.Mod).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        return found;
    }

    private void SelectMod(string dir)
    {
        FinishMove();
        FlushPending();   // before modDir changes, which the save needs to find the file
        EndLivePreview(refreshGame: true);
        brushChangedAt = -1;
        movePart = null;
        moveGizmo.Release();
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

        // A pre-v4 folder is read-only to Proteus, so there is nothing useful to offer: every model would
        // be listed, clickable and staged, only for the write to refuse at the end. Answered before the
        // list is built instead — the message says how to fix it, and Penumbra does the fixing.
        // No wait: this runs on the draw thread and only decides what to show. A write still checks properly.
        modIsLegacy = PenumbraModMeta.IsLegacyFolder(root, waitIfHeld: false);
        if (modIsLegacy) return;

        // Models the mod PUBLISHES, not files lying in its folder. That is the list that matters: a model
        // nothing redirects to is dead weight the author left behind, and — the part that decides the whole
        // feature — a published model comes with the game path it claims, which is where the item's IMC
        // identity is read from when the switch is finally written.
        redirects = PenumbraModMeta.ReadAllRedirects(root);

        // Grouped by item, but WITHIN an item left in the order the mod declares them, which is what the
        // author's own group and option order is. That order is the whole point once a row is labelled by
        // its option: sizes do not sort alphabetically into size order, and sorting on Source turned a
        // small/medium/large list into large/medium/small. OrderBy is a stable sort, so dropping the
        // secondary key is all it takes to keep the declaration order ReadAllRedirects already preserves.
        models = redirects
            .Where(r => r.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        modelLabels = ModelLabels(models);
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

            for (int i = 0; i < models.Count; i++)
            {
                // Green for the file the character is drawing right now — the option actually selected in
                // Penumbra for what is being worn. A mod offering five sizes of one garment lists five rows
                // that differ by one word, and the one that matters is the one on screen.
                bool worn = wornModels.Contains(models[i].File.Replace('\\', '/'));
                bool picked;
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Ok, worn))
                    picked = ImGui.Selectable(modelLabels[i] + "##m" + i, i == modelIndex);
                if (picked && i != modelIndex) SelectModel(i);
            }
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// What one model is called in the picker.
    /// <para/>
    /// The MOD'S OWN label leads — "Pant Size / Small" — because that is the choice being made. A mod
    /// publishes one game path from several files precisely so the wearer can pick between them, and those
    /// alternatives are almost always sizes; leading with the slot and set id put the one word that
    /// distinguishes the rows ("Small") last, and a narrow combo cut it off.
    /// <para/>
    /// The slot is appended only to break a tie, since a mod with one garment in five sizes needs it on none
    /// of them. See <see cref="ModelLabels"/>.
    /// </summary>
    internal static string ModelLabel(PenumbraModMeta.Redirect r)
        => r.Source.Length > 0 ? r.Source : SlotOf(r);

    /// <summary>The slot and set a model path names — "Legs — e0488" — or its file name if it names neither.</summary>
    internal static string SlotOf(PenumbraModMeta.Redirect r)
        => ContentSlot.Parse(r.GamePath) is { } p ? $"{p.Label} — {p.SetTag}" : Path.GetFileName(r.GamePath);

    /// <summary>
    /// One label per model, disambiguated only where it has to be.
    /// <para/>
    /// Two entries can share an option name — a mod whose "Small" option supplies both a top and a pair of
    /// trousers gives two rows reading "Sizes / Small" — and a picker with two identical rows is worse than
    /// a verbose one. So the slot is appended to every member of a colliding set, and to nothing else.
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
        // A pending edit belongs to the model being replaced. If its save fails it cannot follow onto the next
        // one — its bytes and its undo history go with the old model — so the waiting flag is dropped either
        // way; the failure is already on the status line.
        FinishMove();
        FlushPending();
        EndLivePreview(refreshGame: true);
        brushChangedAt = -1;
        brushBase = null;
        movePart = null;
        moveGizmo.Release();
        // A new solve starts from the file as it is now, so the other sizes must too. Replaced, not cleared — see sizeBases.
        sizeBases = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        modelIndex = index;
        ticked.Clear();
        expanded.Clear();
        freeLetters = 0;
        // Staged switches name PARTS BY LABEL, and a label means something different on a different model —
        // "1.1.3" is whatever the third island of that model's first submesh happens to be. Carrying them
        // across would write one model's switch onto another's geometry, and any label the new model does
        // not have would be dropped silently under a green "Done".
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
        // The brush state belongs to the model, so it goes when the model does. Not carried across for the
        // same reason staged switches are not: a displacement is per vertex, and another model's vertices
        // are not these.
        volume = parts != null ? new MeshVolumeSolve(parts) : null;
        windAt = volume != null ? volume.WindAt : null;
        viewport.PositionOverride = null;
        partOfVertex = parts != null ? BuildPartOfVertex(parts) : [];
        ApplyLocks();

        if (parts != null) viewport.Show(ViewportKey, parts);
        else viewport.Clear();
    }

    // ── locked parts ────────────────────────────────────────────────────────

    /// <summary>The open model's locked labels, created on first use.</summary>
    private HashSet<string> Locks
    {
        get
        {
            if (!lockedParts.TryGetValue(ViewportKey, out var set))
                lockedParts[ViewportKey] = set = new HashSet<string>(StringComparer.Ordinal);
            return set;
        }
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
        if (parts is not { } model || model.Parts.FirstOrDefault(p => p.Label == label) is not { } part || IsSkin(part))
            return;
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

    /// <summary>Hand the locks to the solve and the viewer.</summary>
    private void ApplyLocks()
    {
        if (parts == null) return;
        var locks = Locks;
        viewport.Locked = locks;
        viewport.Recolour();
        if (volume == null) return;
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
    private int TickedVersion()
    {
        int h = ticked.Count;
        foreach (var label in ticked) h ^= StringComparer.Ordinal.GetHashCode(label) * 16777619;
        return h;
    }

    // ── move ────────────────────────────────────────────────────────────────

    private readonly Action<int> moveClickedFn;
    private readonly Func<int, bool> moveTickedFn;
    private readonly Func<bool> gizmoCaptureFn;

    /// <summary>The part the Move tool has chosen, or null.</summary>
    private ModelPart? MovePart()
        => movePart == null ? null : parts?.Parts.FirstOrDefault(p => p.Label == movePart);

    /// <summary>Choose the part to move, from the model, the list or the character. Skin and locked parts cannot be.</summary>
    private void SelectMovePart(string label)
    {
        if (volume is { Moving: true }) return;
        if (parts?.Parts.FirstOrDefault(p => p.Label == label) is not { } part || IsSkin(part) || IsLocked(part)) return;
        movePart = label;
        viewport.Recolour();
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

    /// <summary>The live tint's cache key for the chosen part — salted, so it never matches a ticked set's.</summary>
    private int MoveVersion() => StringComparer.Ordinal.GetHashCode(movePart ?? "") ^ 0x5BD1E995;

    private IReadOnlySet<string> MoveSelection()
    {
        moveSelectionSet.Clear();
        if (movePart != null) moveSelectionSet.Add(movePart);
        return moveSelectionSet;
    }

    private readonly HashSet<string> moveSelectionSet = new(StringComparer.Ordinal);

    /// <summary>The middle of the chosen part's bounds as it now stands — where the gizmo sits. Null with no part.</summary>
    private Vector3? MovePivot()
    {
        if (volume == null || MovePart() is not { } part || part.Triangles.Length == 0) return null;
        var p = volume.Positions();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int v in part.Triangles)
        {
            if (v < 0 || v * 3 + 2 >= p.Length) continue;
            var at = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return min.X <= max.X ? (min + max) * 0.5f : null;
    }

    /// <summary>
    /// Turn the gizmo's drag into a move of the chosen part — the Move tool's <see cref="PumpBrush"/>. The gizmo was
    /// driven earlier this frame by whichever surface shows it: the model view as it drew, the character before any
    /// window did.
    /// </summary>
    private void PumpMove()
    {
        if (volume == null || tool != Tool.Move || moveGizmo.Frame != ImGui.GetFrameCount()) return;

        if (moveGizmo.Started && MovePart() is { } part)
        {
            if (volume.BeginMove(part.Triangles, moveAdjacent, moveFalloffMm / 1000f) == 0)
            {
                status = Strings.Parts.MoveNothingFree;
                statusIsError = true;
            }
        }

        if (!volume.Moving) return;

        volume.MoveTo(moveGizmo.Offset);
        viewport.PositionOverride = volume.Positions();
        viewport.GeometryChanged();
        previewDirty = !showModelView;

        if (moveGizmo.Ended) FinishMove();
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
        if (showModelView) ImGui.TextDisabled(ps.MoveHelp);
        else if (liveBrush.Problem is { } problem) ImGui.TextColored(ProteusStyle.Warn, problem);
        else ImGui.TextDisabled(ps.MoveLiveHint);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (MovePart() is { } part)
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
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(ps.MoveFalloffTip);

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

    private string ViewportKey => modDir + "|" + (modelIndex >= 0 ? models[modelIndex].File : "");

    private string? ModRoot()
    {
        var root = penumbra.GetModDirectory();
        return root == null || modDir == null ? null : Path.Combine(root, modDir);
    }

    // ── the model, and the list beside it ───────────────────────────────────

    /// <summary>
    /// The model on the left, the parts on the right, each driving the other: clicking the model ticks a
    /// part, hovering a row lights that part up on the model.
    /// <para/>
    /// The list is still here, and not just as a fallback. It is the only place that can show a part which
    /// is entirely hidden behind another, say what material a part draws with, or mark the parts that
    /// already answer to a switch of the author's — where a switch added here stacks, and both have to be
    /// on for the part to draw.
    /// </summary>
    private void DrawParts(float height)
    {
        var ps = Strings.Parts;
        var model = parts!;

        viewport.Show(ViewportKey, model);
        viewport.Selected = tool == Tool.Move ? MoveSelection() : ticked;

        // Told every frame rather than on change: the mode also resets when a model is picked, and one place
        // that always states the truth is cheaper to reason about than several that update it.
        viewport.Mode = tool switch
        {
            Tool.Navigate => PartViewport.ViewportMode.Navigate,
            Tool.Move     => PartViewport.ViewportMode.Move,
            _             => PartViewport.ViewportMode.Brush,
        };
        viewport.GizmoCapture = gizmoCaptureFn;
        viewport.BrushRadius = tool is Tool.Navigate or Tool.Move ? 0f : ActiveRadiusMm / 1000f;
        viewport.VertexScalar = tool == Tool.Wind ? windAt : null;
        viewport.MirrorBrush = mirrorBrush;

        // The share cap is on the image's WIDTH, not on the row's height, and that is load-bearing. Capping
        // the height by the available WIDTH would couple the row to avail.X — which shrinks by the scrollbar
        // width the moment a scrollbar appears — and that closes a loop: scrollbar appears, row shortens,
        // content fits, scrollbar goes, row grows, scrollbar appears. A per-frame flicker exactly at the size
        // where the content just barely fits. Capping only the image leaves the row's height a function of
        // avail.Y alone; a wide-and-short model is simply letterboxed shorter than the list beside it, which
        // reads fine because SameLine tops them out together.
        // Under a brush the part list stays beside the model too: there its boxes lock parts against the brush, and
        // a click on the model — which paints — only reaches a part with Shift held (a Shift-drag still pans).
        bool brushing = tool is not (Tool.Navigate or Tool.Move);
        float width = MathF.Min(height * PartViewport.DefaultAspect, ImGui.GetContentRegionAvail().X * 0.55f);
        if (viewport.Draw(model, new Vector2(width, height)) is { } clicked)
        {
            if (tool == Tool.Move) SelectMovePart(clicked);
            else if (brushing) ToggleLock(clicked);
            else Toggle(clicked);
        }

        // Right after the image, so the gizmo is drawn over it, in the same window's draw list.
        if (tool == Tool.Move && MovePivot() is { } pivot)
        {
            moveGizmo.Update(pivot, viewport.ModelToScreen, viewport.ScreenRay, ImGui.GetMousePos(),
                             mouseAllowed: viewport.PointerOverModel, pressed: viewport.Pressed, down: viewport.Held,
                             background: false);
            if (moveGizmo.Capturing) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }

        // Two different things to say, and only one of them is an apology. A part the author already
        // switches takes the click normally — the tooltip is there to explain that the new switch will
        // stack rather than replace. A part with an unreadable tag is the one the model still lights up,
        // hand-cursors and then quietly absorbs, so without this it says nothing at all.
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

        // A horizontal scrollbar, because the width is no longer the window's to choose. A row is a checkbox
        // with the part's label, its material's filename, a triangle count and sometimes an expander; the
        // window used to simply widen (up to its maximum) until the longest one fitted. Dragged narrow it
        // cannot, and a child clips silently — a scrollbar at least admits there is more to read.
        float listWidth = ImGui.GetContentRegionAvail().X;
        using (var group = ImRaii.Child("##partList", new Vector2(listWidth, height), false,
                                        ImGuiWindowFlags.HorizontalScrollbar))
        {
            if (group)
            {
                // Wrapped at the child's OWN visible width, not at PushTextWrapPos(0). Inside a horizontally
                // scrolling window, wrap-pos 0 means "the content edge", which includes last frame's widest
                // row — so the text would stop wrapping, become the widest row itself, and ratchet the scroll
                // range wider every frame. An explicit position is immune to what the rows do.
                // Floored, because a NEGATIVE wrap position means "do not wrap" to ImGui — the one value that
                // would quietly reinstate the ratchet this is here to prevent.
                float wrapAt = MathF.Max(
                    listWidth - ImGui.GetStyle().WindowPadding.X * 2f - ImGui.GetStyle().ScrollbarSize,
                    ProteusStyle.S(80f));

                ImGui.PushTextWrapPos(wrapAt);
                ImGui.TextDisabled(tool switch
                {
                    Tool.Navigate => ps.ClickTip,
                    Tool.Move     => ps.MoveListTip,
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
    /// One row per part, with a submesh's islands folded away behind an expander.
    /// <para/>
    /// Folded because a submesh can hold a great many: a pair of trousers turned out to carry 78 straps in
    /// one, and listing them all by default buries every other part of the garment under them. Ticked
    /// islands are always shown whatever the expander says, so a piece clicked on the model always has a
    /// row — otherwise clicking a strap would tick something the list did not admit existed.
    /// </summary>
    private void DrawPartRows(ModelParts model)
    {
        var ps = Strings.Parts;
        string? hoveredRow = null;

        // Under a brush the same rows lock parts instead: TICKED means the brush moves it, unticking locks it.
        // Everything about a switch — what may take one, what the author already switches — is beside the point.
        // Under Move each row chooses the part to move, one at a time.
        bool moving = tool == Tool.Move;
        bool brushing = tool != Tool.Navigate && !moving;

        // Islands per submesh, so a submesh row can say how many it has and whether to draw them.
        var islands = model.Parts.Where(p => p.Island >= 0)
            .GroupBy(p => (p.Mesh, p.Submesh))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var part in model.Parts)
        {
            bool isIsland = part.Island >= 0;
            var owner = (part.Mesh, part.Submesh);

            // A locked island always has a row under a brush, as a ticked one does for a switch — otherwise a
            // Shift-click on a strap would lock something the list did not admit existed.
            bool listed = moving ? movePart == part.Label
                        : brushing ? Locks.Contains(part.Label) : ticked.Contains(part.Label);
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
            else if (brushing)
            {
                bool skin = IsSkin(part);
                bool moves = skin || !IsLocked(part);
                using (ImRaii.Disabled(skin))
                    if (ImGui.Checkbox($"{part.Label}##l_{part.Label}", ref moves))
                        ToggleLock(part.Label);
                if (skin && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(ps.BrushLockSkinTip);
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

            // Marked on the row, not left to the tooltip. The stacking changes what ticking this box means
            // — the part will need the author's switch on as well — and that is worth knowing while
            // choosing, not only after hovering the one row you already suspected.
            if (part.AuthorSwitched && !brushing && !moving)
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
    /// Which tool the drag belongs to, as three radio buttons above the model.
    /// <para/>
    /// An explicit mode rather than a modifier key. Turning the model and painting on it are both continuous
    /// activities, so holding a key for one of them is miserable — and every modifier is already spoken for
    /// anyway, shift on the pan.
    /// </summary>
    private void DrawToolPicker()
    {
        var ps = Strings.Parts;

        foreach (var (value, icon, label, tip) in new[]
                 {
                     (Tool.Move,     FontAwesomeIcon.ArrowsAlt,         ps.ToolMove,     ps.ToolMoveTip),
                     (Tool.Inflate,  FontAwesomeIcon.ExpandArrowsAlt,   ps.ToolInflate,  ps.ToolInflateTip),
                     (Tool.Deflate,  FontAwesomeIcon.CompressArrowsAlt, ps.ToolDeflate,  ps.ToolDeflateTip),
                     (Tool.Relax,    FontAwesomeIcon.Feather,           ps.ToolRelax,    ps.ToolRelaxTip),
                     (Tool.Bridge,   FontAwesomeIcon.Archway,           ps.ToolBridge,   ps.ToolBridgeTip),
                     (Tool.Wind,     FontAwesomeIcon.Wind,              ps.ToolWind,     ps.ToolWindTip),
                     (Tool.Navigate, FontAwesomeIcon.MousePointer,      ps.ToolNavigate, ps.ToolNavigateTip),
                 })
        {
            // Stacked, one per row: the panel is a narrow column, and four buttons side by side do not fit it.
            // An icon button per tool, highlighted when current — the project's "this is the selection" style.
            // IconButtonWithText draws the text itself, so the ###id the label carries for ImGui is cut off
            // and supplied as a pushed id instead; passed whole it would be printed.
            bool clicked;
            var text = label.Split("###")[0];
            using (ImRaii.PushId((int)value))
            using (ProteusStyle.Selected(tool == value))
                clicked = ImGuiComponents.IconButtonWithText(icon, text, FullWidth());

            if (clicked && tool != value)
            {
                // Before leaving the brush, so a pending edit cannot land on top of a switch written in the
                // meantime — a save rewrites the whole model from the bytes the brush was opened on.
                FinishMove();
                FlushPending();
                tool = value;
                // Staged parts are a Pick-parts thing; the brush hides the list, so a selection carried into it
                // would sit there invisibly and reappear half-forgotten on the way back.
                ticked.Clear();
                movePart = null;
                moveGizmo.Release();
                viewport.Recolour();
                viewport.GeometryChanged();   // the wind wash comes and goes with the wind tool
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(value is Tool.Navigate or Tool.Move ? tip : tip + "\n\n" + ps.BrushLockHint);
        }
        ImGui.Spacing();
    }

    /// <summary>
    /// Turn what the viewport reports into strokes on the geometry.
    /// <para/>
    /// Polled rather than event-driven because ImGui is: the viewport says where the brush is and whether it
    /// is down, and this decides what that means. Painting happens per frame while the button is held, and
    /// the expensive passes run off the one frame the stroke ends.
    /// </summary>
    private void PumpBrush()
    {
        if (volume == null || tool is Tool.Navigate or Tool.Move) return;
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
                // Not for wind: the in-place preview does not show it (see SaveBrush), so pushing one per dab is
                // only cost.
                previewDirty = !showModelView && tool != Tool.Wind;
            }
        }

        if (surface.StrokeEnded)
        {
            volume.EndStroke(bridge: tool == Tool.Bridge, wind: tool == Tool.Wind);
            viewport.PositionOverride = volume.Positions();
            viewport.GeometryChanged();
            brushChangedAt = Environment.TickCount64;

            // On the character, the character IS the preview: save and reload the garment as soon as the
            // stroke is done rather than after the viewer's debounce, or the pull would sit invisible.
            if (!showModelView) SaveBrush();
        }
    }

    /// <summary>How long after the last change the brush writes itself into the mod.</summary>
    private const long AutosaveMs = 1500;

    /// <summary>
    /// Save the brush once it has been left alone for <see cref="AutosaveMs"/>, then have Penumbra reload the
    /// mod and redraw the character so the edit is on screen in game, not just in the viewer.
    /// <para/>
    /// Debounced from the LAST change rather than timed from the first, so a run of strokes saves once at the
    /// end instead of once per stroke — each save is a full rewrite of the model plus a redraw of the
    /// character, which is far too heavy to do between dabs. Never while the brush is held down.
    /// </summary>
    private void TickAutosave()
    {
        if (brushChangedAt < 0 || Editing) return;
        if (Environment.TickCount64 - brushChangedAt < AutosaveMs) return;
        SaveBrush();
    }

    /// <summary>
    /// Save now if anything is waiting. Called before anything that replaces the model on screen or writes to
    /// the same file — a pending edit belongs to that model and those bytes — and by the window when the
    /// Toggles tab stops being drawn, since the autosave timer only runs while it is.
    /// </summary>
    /// <param name="refreshGame">Reload the mod in Penumbra and redraw the character afterwards. False only
    /// while the plugin is being torn down, when the file still has to land but nothing should be poked.</param>
    /// <returns>True when nothing is left waiting — including when there was nothing to save. False means a
    /// save was attempted and failed, and the caller must not go on to write the same file.</returns>
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
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        // A share of the side panel rather than a fixed width, so the label printed to the right of each slider
        // still fits inside the column instead of being clipped by it.
        float w = ImGui.GetContentRegionAvail().X * 0.55f;
        ImGui.SetNextItemWidth(w);
        // Logarithmic, down to a millimetre: a face feature is a few millimetres across and a skirt's swell a few
        // hundred, and on a linear slider the whole of the small end would be its first few pixels.
        if (ImGui.SliderFloat(ps.BrushSize, ref ActiveRadiusMm, MinBrushMm, MaxBrushMm,
                              ActiveRadiusMm < 10f ? "%.1f mm" : "%.0f mm", ImGuiSliderFlags.Logarithmic))
            viewport.Recolour();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushSizeTip + "\n\n" + ps.BrushSizeKeysTip);

        // One Strength slider, meaning a distance for the pull and push brushes and a rate for relax. Separate
        // values underneath, so neither is misread when the tool changes.
        ImGui.SetNextItemWidth(w);
        // Relax and bridge are both a rate — part of a gap closed each moment — but each keeps its own value,
        // since a comfortable relax rate fills a crack with the bridge almost instantly.
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

        // A radius that reaches barely more than one vertex moves a spike rather than a surface, and it
        // looks from the outside exactly like the brush not working. Said against the mesh's OWN resolution,
        // because "20 mm" means something different on a 2,000-triangle skirt and a 60,000-triangle coat.
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
                : tool == Tool.Move ? string.Format(ps.MoveMovedFmt, vol.Worst * 1000f)
                : string.Format(ps.BrushMovedFmt, vol.Worst * 1000f, vol.MaxDisplacement * 1000f));

            // Undo and start-over save and redraw AT ONCE rather than on the debounce. The debounce exists to
            // batch a run of strokes into one save; an undo is a single deliberate click whose whole point is
            // to see the character put back, and seconds of it still wearing the mistake reads as the
            // button not working.
            //
            // Every button full width, one per row, so the column reads as a stack of actions rather than a
            // ragged edge of differently-sized labels.
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
                if (ImGui.Button(tool == Tool.Move ? ps.MoveUndo : ps.BrushUndo, FullWidth()))
                { vol.Undo(); AfterBrushEdit(); SaveBrush(); }

            using (ImRaii.Disabled(!vol.Dirty || vol.Moving))
                if (ImGui.Button(ps.BrushReset, FullWidth())) { vol.Reset(); AfterBrushEdit(); SaveBrush(); }

            // Saving happens on its own: a moment after the last change in the viewer, and the moment a stroke is let
            // go on the character. The button is for not waiting — so on the character, where there is never a wait,
            // it shows only while a save is still owed (one that failed and is being retried), and a disabled one
            // says why rather than looking broken.
            if (showModelView || brushChangedAt >= 0)
            {
                ImGui.Spacing();
                using (ImRaii.Disabled(brushChangedAt < 0))
                    if (ImGui.Button(ps.BrushSave, FullWidth())) SaveBrush();
                ProteusStyle.ReasonTooltip(brushChangedAt < 0 ? ps.BrushSaveNothingTip : ps.BrushSaveTip);
            }

            if (brushChangedAt >= 0) ImGui.TextColored(ProteusStyle.Warn, ps.BrushNotSavedYet);

            // Ctrl- or Shift-armed: it throws away every saved brush edit in the mod. The house style for anything
            // destructive (see PresetBar's delete) — there are no confirmation modals in this plugin.
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

        // From the bytes the brush was opened on, not the file on disk — see Apply. Written even when the
        // edit is now empty, so undoing everything also takes the last save back out of the mod.
        var result = MeshVolumeService.Apply(root, rel, brushBase, volume, writeUntouched: true);
        statusIsError = !result.Ok;
        if (!result.Ok)
        {
            status = result.Message;
            log.Warning("[Proteus] brush: {0}", result.Message);

            // STILL WAITING, re-armed for another full debounce rather than cleared. Clearing it here meant a
            // save that failed — Penumbra holding the file past AtomicWrite's retries — was never tried again,
            // and the "not saved yet" notice vanished, so the viewer went on showing an edit the mod did not
            // have. Retried on the debounce rather than every frame, so a file that stays locked costs one
            // attempt every few seconds instead of one per frame.
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

        // Counted, not silent. A spare left behind means enabling that body slider puts the slots it
        // rewires back where the author had them, which looks exactly like the brush having missed a patch.
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

        // NOT AfterModChange. That re-reads the model and rebuilds everything from it, which would throw
        // away the brush's undo history on every autosave and rebuild the solve on the just-saved bytes —
        // so the next save would add the whole edit on top of itself. The solve, the viewer and the undo
        // stack all stay; only the game needs telling.
        //
        // Reload BEFORE the redraw, in that order: a redraw alone re-resolves the path and is handed back the
        // bytes Penumbra still has in memory — see HatCompatWatcher, which learned it the hard way.
        if (!refreshGame) return;

        // On the character, the preview shows the saved state: a new file, reloaded in place, no redraw. The
        // mod's own file cannot be shown that way — the game hands back the model it has cached under that
        // path — so everywhere else, and wherever Glamourer cannot reload in place (hair, a face), it is a full
        // redraw. Previewed, the mod's reload is marked as our own, or the compositor takes it for a changed base
        // and recomposites and redraws the character after every stroke. A material just rewritten for wind is
        // cached by the game like the model is, and the preview replaces only the model — so that save redraws once.
        // Wind always redraws: the in-place reload shows a moved surface but not new wind, measured in game. Its reload
        // is still our own — wind moves no geometry, so nothing the second skin is cut from has changed.
        bool wind = tool == Tool.Wind;
        bool previewed = !showModelView && !preview.UnsupportedFor(TargetIsCustomizePart) && materialsChanged == 0 && !wind;
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

        // AfterModChange reloads the mod and recomposites, but a recomposite is about skin, not gear — the
        // character would keep the brushed model it has cached until something else redrew it. Same pairing
        // as a save: reload (done above), then redraw.
        if (result.Ok) compositor.RedrawForChangedModel();
    }

    // ── other sizes ─────────────────────────────────────────────────────────

    /// <summary>
    /// Each other size's bytes as they were the first time the brush was applied to it since this model was opened.
    /// Every apply is those bytes plus the WHOLE current edit, the way every save of the model itself is
    /// <see cref="brushBase"/> plus the whole edit — so pressing the button again after more strokes replaces what
    /// the last press wrote instead of stacking on it.
    /// <para/>
    /// Written from the apply's worker thread, hence concurrent — and REPLACED, never cleared, when another model is
    /// opened, so an apply still running for the last model fills the old instance rather than the new one.
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

    /// <summary>
    /// The mod's other files for the open model's game path — its other sizes — one row per file. Asked every frame
    /// the brush panel draws, so worked out once per model list and model.
    /// </summary>
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
    /// Carry the open model's brush edit onto every other size of it in the mod — see <see cref="BrushTransfer"/> —
    /// and save each through the brush's own backup and record, so Undo saved changes takes them back as well.
    /// <para/>
    /// ON A WORKER THREAD. Per size it is a file read, a parse, a nearest-point search per vertex and a rewrite,
    /// which on a garment with several sizes froze the game for seconds when it ran inside the click. Everything
    /// the work needs is gathered here first — the edit itself SNAPSHOTTED, since the user can go on painting the
    /// live solve meanwhile — and what must happen on the framework thread (Penumbra, the status line, a redraw) is
    /// done by <see cref="ConsumeApplySizes"/> once it finishes.
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

    // ── staging ─────────────────────────────────────────────────────────────

    private void DrawStaging()
    {
        var ps = Strings.Parts;
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
        // Listed per item, because that is how they are grouped in Penumbra: a mod with a top and a pair of
        // trousers gets a group each, and "Bow, Belt" on one line would not say which garment either is on.
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
        // The switch is written into the same file the brush saves, and a brush save rewrites the WHOLE model
        // from the bytes the brush was opened on — so a brush edit still waiting would later land on top of
        // the switch and erase it. Saved first, and if that save fails the switch is not written at all.
        if (!FlushPending()) return;
        var ps = Strings.Parts;
        if (parts == null || modelIndex < 0 || ModRoot() is not { } root) return;

        var byLabel = parts.Parts.ToDictionary(p => p.Label, StringComparer.Ordinal);
        var plans = pending
            .Select(t => new MeshToggleService.Plan(
                t.Name, t.Parts.Where(byLabel.ContainsKey).Select(l => byLabel[l]).ToList()))
            .Where(p => p.Parts.Count > 0)
            .ToList();

        var result = MeshToggleService.Write(
            root, models[modelIndex], parts, plans, redirects,
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

        // Same reason as Commit, from the other side. Without this, a brush edit waiting to save was flushed
        // by the model reload AFTER the restore — from bytes that still carried the switches — and put the
        // split submeshes and their attributes straight back into a mod whose group had just been deleted.
        // Saved first, so the restore order check sees the brush as the later edit and refuses to undo
        // under it; and if the save fails, nothing is restored.
        if (!FlushPending()) return;

        var result = MeshToggleService.Revert(root);
        statusIsError = !result.Ok;
        status = result.Ok ? string.Format(Strings.Parts.RevertedFmt, result.FilesPatched) : result.Message;

        pending.Clear();
        ticked.Clear();
        AfterModChange(root);
    }

    /// <summary>
    /// Re-read everything the mod's files say, and make Penumbra do the same.
    /// <para/>
    /// The model on disk has changed, so the part list, the viewport and the record are all describing a
    /// file that no longer exists in that form — and Penumbra is still serving the old one until it is told
    /// otherwise. A split in particular renumbers parts, so a stale list would tick the wrong ones.
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
