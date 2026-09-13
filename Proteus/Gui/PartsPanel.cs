using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
    }

    private Tool tool = Tool.Navigate;

    /// <summary>
    /// The editable geometry of the model on screen, or null before one is picked. Rebuilt only when the
    /// model changes — the weld and the boundary scan depend on topology, which a stroke never alters.
    /// </summary>
    private MeshVolumeSolve? volume;

    /// <summary>Brush radius and per-dab strength, both in millimetres because that is how the problem is
    /// described: "the hip pokes through by about a millimetre".</summary>
    private float brushRadiusMm = 200f, brushStrengthMm = 0.05f;

    /// <summary>
    /// The bridge brush's own size, smaller than the others'. A bridge spans the hollow under the brush, so a
    /// brush far wider than the crack reaches past it onto the curves either side and bridges between those
    /// too; starting it near the width of the thing it is for keeps the span where it was meant.
    /// </summary>
    private float bridgeRadiusMm = 70f;

    /// <summary>The size the current tool paints with — the bridge keeps its own, see above.</summary>
    private ref float ActiveRadiusMm => ref tool == Tool.Bridge ? ref bridgeRadiusMm : ref brushRadiusMm;

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

    /// <summary>Waiting for a click on the character to choose the garment to edit.</summary>
    private bool picking;

    /// <summary>Where strokes come from this frame.</summary>
    private IBrushSurface Surface => showModelView ? viewport : liveBrush;

    public PartsPanel(
        PenumbraBridge penumbra, CompositorService compositor, PartViewport viewport, LiveBrush liveBrush,
        TextureLoader textureLoader, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.viewport = viewport;
        this.liveBrush = liveBrush;
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

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(ps.Intro);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

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
        float height = ProteusStyle.S(360f);
        if (fillHeight)
            height = MathF.Max(ImGui.GetContentRegionAvail().Y - reserveBelow - ProteusStyle.S(4f),
                               ProteusStyle.S(200f));

        // The window grows for the viewer only; controls alone fit the size it already is.
        ShowingModel = showModelView;
        HandleUndoShortcut();

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
                    viewport.Recolour();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.Parts.ShowModelViewTip);
                ImGui.Separator();

                DrawToolPicker();
                ImGui.Separator();
                if (tool == Tool.Navigate) DrawStaging(); else DrawBrush();
            }
        }

        if (showModelView)
        {
            ImGui.SameLine();
            DrawParts(height);
        }
        else if (tool == Tool.Navigate)
        {
            // Without the viewer, picking parts for a switch is done from the list alone.
            ImGui.SameLine();
            DrawPartList(parts, height);
        }
        else if (volume != null && brushBase != null && ModRoot() is { } root)
        {
            liveBrush.ArmBrush(Path.Combine(root, models[modelIndex].File.Replace('/', Path.DirectorySeparatorChar)),
                               brushBase, volume, ActiveRadiusMm / 1000f);
        }

        PumpBrush();
    }

    /// <summary>
    /// The button that chooses a garment by clicking it on the character, and the pick itself.
    /// <para/>
    /// Above the mod picker because it replaces it: finding the right mod among hundreds, then the right model
    /// among its sizes, is the slowest part of fixing a clip, and the character already knows the answer.
    /// </summary>
    private void DrawLivePick()
    {
        var ps = Strings.Parts;
        using (ProteusStyle.Selected(picking))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Crosshairs, picking ? ps.LivePicking : ps.LivePick))
                picking = !picking;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.LivePickTip);

        if (picking && penumbra.GetModDirectory() is { } modsRoot)
            liveBrush.ArmPick(modsRoot, OnLivePicked);
        ImGui.Spacing();
    }

    /// <summary>A garment was clicked on the character: open its mod and model.</summary>
    private void OnLivePicked(string file)
    {
        picking = false;
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
        if (tool == Tool.Navigate) tool = Tool.Inflate;   // they clicked it to fix it
    }

    /// <summary>Width of the tool panel left of the model, before UI scaling.</summary>
    private const float SidePanelWidth = 250f;

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
        if (tool == Tool.Navigate || volume is not { CanUndo: true } vol || Surface.Painting) return;
        // Painting on the character clicks into the game world, which takes focus away from every window —
        // so there, "no window has focus" counts too. Ctrl+Z means nothing to the game itself.
        bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                    || (!showModelView && !ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow));
        if (!focused) return;
        var io = ImGui.GetIO();
        if (io.WantTextInput || !io.KeyCtrl || !ImGui.IsKeyPressed(ImGuiKey.Z, false)) return;

        vol.Undo();
        AfterBrushEdit();
        SaveBrush();
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
            if (picked && dir != modDir) SelectMod(dir);
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
        FlushPending();   // before modDir changes, which the save needs to find the file
        brushChangedAt = -1;
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
        modIsLegacy = PenumbraModMeta.IsLegacyFolder(root);
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
        FlushPending();
        brushChangedAt = -1;
        brushBase = null;
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
        viewport.PositionOverride = null;

        if (parts != null) viewport.Show(ViewportKey, parts);
        else viewport.Clear();
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
        viewport.Selected = ticked;

        // Told every frame rather than on change: the mode also resets when a model is picked, and one place
        // that always states the truth is cheaper to reason about than several that update it.
        viewport.Mode = tool == Tool.Navigate
            ? PartViewport.ViewportMode.Navigate
            : PartViewport.ViewportMode.Brush;
        viewport.BrushRadius = tool == Tool.Navigate ? 0f : ActiveRadiusMm / 1000f;

        // The share cap is on the image's WIDTH, not on the row's height, and that is load-bearing. Capping
        // the height by the available WIDTH would couple the row to avail.X — which shrinks by the scrollbar
        // width the moment a scrollbar appears — and that closes a loop: scrollbar appears, row shortens,
        // content fits, scrollbar goes, row grows, scrollbar appears. A per-frame flicker exactly at the size
        // where the content just barely fits. Capping only the image leaves the row's height a function of
        // avail.Y alone; a wide-and-short model is simply letterboxed shorter than the list beside it, which
        // reads fine because SameLine tops them out together.
        // Under a brush the part list is hidden and the model takes the whole row: the list is for choosing
        // parts to switch, and while painting it is only in the way of the thing being painted.
        bool brushing = tool != Tool.Navigate;
        float width = brushing
            ? ImGui.GetContentRegionAvail().X
            : MathF.Min(height * PartViewport.DefaultAspect, ImGui.GetContentRegionAvail().X * 0.55f);
        if (viewport.Draw(model, new Vector2(width, height)) is { } clicked) Toggle(clicked);
        if (brushing) return;

        // Two different things to say, and only one of them is an apology. A part the author already
        // switches takes the click normally — the tooltip is there to explain that the new switch will
        // stack rather than replace. A part with an unreadable tag is the one the model still lights up,
        // hand-cursors and then quietly absorbs, so without this it says nothing at all.
        if (viewport.PointerOverModel && viewport.Hovered is { } hot
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
                ImGui.TextDisabled(ps.ClickTip);
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

        // Islands per submesh, so a submesh row can say how many it has and whether to draw them.
        var islands = model.Parts.Where(p => p.Island >= 0)
            .GroupBy(p => (p.Mesh, p.Submesh))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var part in model.Parts)
        {
            bool isIsland = part.Island >= 0;
            var owner = (part.Mesh, part.Submesh);

            if (isIsland && !expanded.Contains(owner) && !ticked.Contains(part.Label)) continue;
            if (isIsland) ImGui.Indent(ProteusStyle.S(12f));

            bool on = ticked.Contains(part.Label);
            using (ImRaii.Disabled(!part.Toggleable))
                if (ImGui.Checkbox($"{part.Label}##p_{part.Label}", ref on))
                    Toggle(part.Label);

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) hoveredRow = part.Label;

            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(ps.RowFmt,
                Path.GetFileName(part.Material.TrimStart('/')), part.TriangleCount));
            if (ImGui.IsItemHovered()) hoveredRow = part.Label;

            // Marked on the row, not left to the tooltip. The stacking changes what ticking this box means
            // — the part will need the author's switch on as well — and that is worth knowing while
            // choosing, not only after hovering the one row you already suspected.
            if (part.AuthorSwitched)
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

            if (hoveredRow == part.Label)
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
                     (Tool.Navigate, FontAwesomeIcon.MousePointer,      ps.ToolNavigate, ps.ToolNavigateTip),
                     (Tool.Inflate,  FontAwesomeIcon.ExpandArrowsAlt,   ps.ToolInflate,  ps.ToolInflateTip),
                     (Tool.Deflate,  FontAwesomeIcon.CompressArrowsAlt, ps.ToolDeflate,  ps.ToolDeflateTip),
                     (Tool.Relax,    FontAwesomeIcon.Feather,           ps.ToolRelax,    ps.ToolRelaxTip),
                     (Tool.Bridge,   FontAwesomeIcon.Archway,           ps.ToolBridge,   ps.ToolBridgeTip),
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
                FlushPending();
                tool = value;
                // Staged parts are a Pick-parts thing; the brush hides the list, so a selection carried into it
                // would sit there invisibly and reappear half-forgotten on the way back.
                ticked.Clear();
                viewport.Recolour();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
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
        if (volume == null || tool == Tool.Navigate) return;
        var surface = Surface;

        if (surface.Painting && surface.Cursor is { } at)
        {
            float radius = ActiveRadiusMm / 1000f;
            int moved = tool switch
            {
                Tool.Relax  => volume.Relax(at, radius, relaxRatePercent / 100f),
                Tool.Bridge => volume.Bridge(at, radius, bridgeRatePercent / 100f, surface.ToViewer),
                _           => volume.Paint(at, radius,
                                            brushStrengthMm / 1000f * (tool == Tool.Deflate ? -1f : 1f)),
            };
            if (moved > 0)
            {
                viewport.PositionOverride = volume.Positions();
                viewport.GeometryChanged();
            }
        }

        if (surface.StrokeEnded)
        {
            volume.EndStroke(bridge: tool == Tool.Bridge);
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
        if (brushChangedAt < 0 || Surface.Painting) return;
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
        if (ImGui.SliderFloat(ps.BrushSize, ref ActiveRadiusMm, 20f, 300f, "%.0f mm"))
            viewport.Recolour();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushSizeTip);

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
        else
        {
            ImGui.SliderFloat(ps.BrushStrength, ref brushStrengthMm, 0.01f, 0.5f, "%.2f mm");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushStrengthTip);
        }

        // A radius that reaches barely more than one vertex moves a spike rather than a surface, and it
        // looks from the outside exactly like the brush not working. Said against the mesh's OWN resolution,
        // because "20 mm" means something different on a 2,000-triangle skirt and a 60,000-triangle coat.
        if (volume is { MeanEdge: > 0f } v && ActiveRadiusMm / 1000f < v.MeanEdge * 1.5f)
            ImGui.TextColored(ProteusStyle.Warn,
                              string.Format(ps.BrushTooSmallFmt, v.MeanEdge * 1000f));

        if (volume is { } vol)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(vol.Dirty
                ? string.Format(ps.BrushMovedFmt, vol.Worst * 1000f, MeshVolumeSolve.MaxDisplacement * 1000f)
                : ps.BrushUntouched);

            // Undo and start-over save and redraw AT ONCE rather than on the debounce. The debounce exists to
            // batch a run of strokes into one save; an undo is a single deliberate click whose whole point is
            // to see the character put back, and seconds of it still wearing the mistake reads as the
            // button not working.
            //
            // Every button full width, one per row, so the column reads as a stack of actions rather than a
            // ragged edge of differently-sized labels.
            using (ImRaii.Disabled(!vol.CanUndo))
                if (ImGui.Button(ps.BrushUndo, FullWidth())) { vol.Undo(); AfterBrushEdit(); SaveBrush(); }

            using (ImRaii.Disabled(!vol.Dirty))
                if (ImGui.Button(ps.BrushReset, FullWidth())) { vol.Reset(); AfterBrushEdit(); SaveBrush(); }

            // Saving happens on its own a few seconds after the last change. The button is for not waiting.
            ImGui.Spacing();
            using (ImRaii.Disabled(brushChangedAt < 0))
                if (ImGui.Button(ps.BrushSave, FullWidth())) SaveBrush();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushSaveTip);

            if (brushChangedAt >= 0) ImGui.TextColored(ProteusStyle.Warn, ps.BrushNotSavedYet);

            if (brushSaved > 0)
            {
                ImGui.Spacing();
                if (ImGui.Button(ps.BrushRevert, FullWidth())) RevertBrush();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushRevertTip);
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

        // Counted, not silent. A spare left behind means enabling that body slider puts the slots it
        // rewires back where the author had them, which looks exactly like the brush having missed a patch.
        if (result.UnmappedSpares > 0)
        {
            status += "\n" + string.Format(Strings.Parts.BrushSparesFmt, result.UnmappedSpares);
            log.Warning("[Proteus] brush: {0} shape values could not be carried in {1}",
                        result.UnmappedSpares, rel);
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
        penumbra.ReloadModDirectory(modDir);
        if (showModelView) compositor.RedrawForChangedModel();
        else compositor.ReloadChangedGear();
    }

    private void RevertBrush()
    {
        if (ModRoot() is not { } root) return;
        brushChangedAt = -1;   // an edit waiting to save must not land on top of the restore

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
