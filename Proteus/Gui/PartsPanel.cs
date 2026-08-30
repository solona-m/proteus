using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    private readonly HashSet<string> ticked = new(StringComparer.Ordinal);
    /// <summary>Submeshes whose islands are listed out. See <see cref="DrawPartRows"/>.</summary>
    private readonly HashSet<(int Mesh, int Submesh)> expanded = [];
    private string toggleName = string.Empty;
    private readonly List<(string Name, List<string> Parts)> pending = [];

    private MeshToggleRecord? existing;
    private string? status;
    private bool statusIsError;

    public PartsPanel(
        PenumbraBridge penumbra, CompositorService compositor, PartViewport viewport,
        TextureLoader textureLoader, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.viewport = viewport;
        this.textureLoader = textureLoader;
        this.log = log;
    }

    /// <summary>Drop the mod list so the next frame re-reads it — wired to the window's Refresh.</summary>
    public void Refresh() => mods = null;

    public void Draw()
    {
        var ps = Strings.Parts;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(ps.Intro);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        DrawModPicker();
        if (modDir == null) return;

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
        DrawParts();
        ImGui.Separator();
        DrawStaging();
    }

    // ── pickers ─────────────────────────────────────────────────────────────

    private void DrawModPicker()
    {
        var ps = Strings.Parts;
        mods ??= penumbra.GetAllMods() ?? [];

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
        if (appearing) modFilter = "";
        ImGui.SetNextItemWidth(-1);
        if (appearing) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##partsFilter", Strings.Export.FilterHint, ref modFilter, 64);
        ImGui.Separator();

        int shown = 0;
        foreach (var (dir, label) in mods.OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase))
        {
            // Folder as well as name. The two routinely differ — Penumbra's folder is a sanitised form of
            // the name, and either can be renamed — so filtering on the label alone hides mods someone is
            // searching for by folder.
            if (modFilter.Length > 0
                && label?.Contains(modFilter, StringComparison.OrdinalIgnoreCase) != true
                && dir?.Contains(modFilter, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            shown++;
            // ##dir: two mods can share a display name, and duplicate ImGui ids would route the click to
            // the wrong row.
            if (ImGui.Selectable($"{label}##{dir}", dir == modDir) && dir != modDir)
                SelectMod(dir);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(dir);
        }
        if (shown == 0)
            ImGui.TextDisabled(string.Format(Strings.Export.NoMatchFmt, modFilter));

        ImGui.EndCombo();
    }

    private void SelectMod(string dir)
    {
        modDir = dir;
        modelIndex = -1;
        parts = null;
        modelUnreadable = false;
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
            for (int i = 0; i < models.Count; i++)
                if (ImGui.Selectable(modelLabels[i] + "##m" + i, i == modelIndex) && i != modelIndex)
                    SelectModel(i);
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
            modelUnreadable = parts == null;
            freeLetters = parts == null ? 0 : ModelPartReader.FreeLetters(parts.AttributeNames).Count;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] parts: could not read {0}", models[index].File);
            modelUnreadable = true;
        }
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
    /// is entirely hidden behind another, say what material a part draws with, or say that the author has
    /// already put one behind a switch of their own.
    /// </summary>
    private void DrawParts()
    {
        var ps = Strings.Parts;
        var model = parts!;

        viewport.Show(ViewportKey, model);
        viewport.Selected = ticked;

        float height = ProteusStyle.S(360f);
        if (viewport.Draw(model, height) is { } clicked) Toggle(clicked);

        // The model gives every sign that a click will work — the part lights up, the cursor becomes a hand
        // — and then quietly absorbs it when the author already gates that geometry. The list says so with a
        // disabled checkbox and a tooltip; without this the model says nothing at all.
        if (viewport.PointerOverModel && viewport.Hovered is { } hot
            && model.Parts.FirstOrDefault(p => p.Label == hot) is { Toggleable: false })
            ImGui.SetTooltip(ps.AlreadyGatedTip);

        ImGui.SameLine();
        using (var group = ImRaii.Child("##partList", new Vector2(0, height)))
        {
            if (group)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextDisabled(ps.ClickTip);
                ImGui.PopTextWrapPos();

                foreach (var (label, count) in model.ShatteredSubmeshes)
                {
                    ImGui.PushTextWrapPos(0);
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

            if (hoveredRow == part.Label && !part.Toggleable)
                ImGui.SetTooltip(ps.AlreadyGatedTip);
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

        ImGui.SetNextItemWidth(ProteusStyle.S(220f));
        ImGui.InputText(ps.ToggleName, ref toggleName, 64);
        ImGui.SameLine();

        bool canAdd = left > 0 && ticked.Count > 0 && !string.IsNullOrWhiteSpace(toggleName);
        using (ImRaii.Disabled(!canAdd))
            if (ImGui.Button(ps.AddBtn))
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
            ImGui.TextUnformatted(string.Format(ps.PendingFmt, name, string.Join(", ", list)));
            ImGui.SameLine();
            if (ImGui.Button($"{ps.RemoveBtn}##rm{i}")) { pending.RemoveAt(i); break; }
        }

        ImGui.Spacing();
        if (ImGui.Button(ps.WriteBtn)) Commit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.WriteTip);
        ImGui.SameLine();
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
        viewport.Clear();   // so it rebuilds its pickable set against the edited model
        if (modelIndex >= 0) SelectModel(modelIndex);

        if (modDir != null) penumbra.ReloadModDirectory(modDir);
        compositor.TriggerRecomposite("parts-written");
    }

}
