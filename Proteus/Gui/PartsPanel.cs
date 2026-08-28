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
public sealed class PartsPanel : IDisposable
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly PartSilhouette silhouette;
    private readonly TextureLoader textureLoader;
    private readonly IPluginLog log;

    private Dictionary<string, string>? mods;

    private string? modDir;
    private List<PenumbraModMeta.Redirect> models = [];
    private int modelIndex = -1;

    private byte[]? modelBytes;
    private ModelParts? parts;
    private bool modelUnreadable;

    private readonly HashSet<string> ticked = new(StringComparer.Ordinal);
    private string toggleName = string.Empty;
    private readonly List<(string Name, List<string> Parts)> pending = [];
    private bool isolating;

    private MeshToggleRecord? existing;
    private string? status;
    private bool statusIsError;

    public PartsPanel(
        PenumbraBridge penumbra, CompositorService compositor, PartSilhouette silhouette,
        TextureLoader textureLoader, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.silhouette = silhouette;
        this.textureLoader = textureLoader;
        this.log = log;
    }

    /// <summary>Drop the mod list so the next frame re-reads it — wired to the window's Refresh.</summary>
    public void Refresh() => mods = null;

    public void Dispose() => ClearIsolate();

    public void Draw()
    {
        var ps = Strings.Parts;
        silhouette.NewFrame();

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

        ImGui.SetNextItemWidth(ProteusStyle.S(340f));
        var current = modDir != null && mods.TryGetValue(modDir, out var name) ? name : ps.PickMod;
        if (ImGui.BeginCombo(ps.Mod + "##partsMod", current))
        {
            foreach (var (dir, label) in mods.OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase))
                if (ImGui.Selectable(label + "##" + dir, dir == modDir) && dir != modDir)
                    SelectMod(dir);
            ImGui.EndCombo();
        }
    }

    private void SelectMod(string dir)
    {
        ClearIsolate();
        modDir = dir;
        modelIndex = -1;
        parts = null;
        modelBytes = null;
        modelUnreadable = false;
        ticked.Clear();
        pending.Clear();

        models = [];
        status = null;
        var root = ModRoot();
        if (root == null) return;

        existing = MeshToggleService.ReadRecord(root);

        // Models the mod PUBLISHES, not files lying in its folder. That is the list that matters: a model
        // nothing redirects to is dead weight the author left behind, and — the part that decides the whole
        // feature — a published model comes with the game path it claims, which is where the item's IMC
        // identity is read from when the switch is finally written.
        models = PenumbraModMeta.ReadAllRedirects(root)
            .Where(r => r.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.GamePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        var current = modelIndex >= 0 ? ModelLabel(models[modelIndex]) : ps.PickModel;
        if (ImGui.BeginCombo(ps.Model + "##partsModel", current))
        {
            for (int i = 0; i < models.Count; i++)
                if (ImGui.Selectable(ModelLabel(models[i]) + "##m" + i, i == modelIndex) && i != modelIndex)
                    SelectModel(i);
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// The slot and item a model path names, falling back to the path's own file name. The source — which
    /// option supplies it — is appended only when there IS one, so a mod with a single always-on model reads
    /// as one plain row.
    /// </summary>
    private static string ModelLabel(PenumbraModMeta.Redirect r)
    {
        var name = ContentSlot.Parse(r.GamePath) is { } p
            ? $"{p.Label} — {p.SetTag}"
            : Path.GetFileName(r.GamePath);
        return r.Source.Length > 0 ? $"{name}  ({r.Source})" : name;
    }

    private void SelectModel(int index)
    {
        ClearIsolate();
        modelIndex = index;
        ticked.Clear();
        parts = null;
        modelUnreadable = false;

        var root = ModRoot();
        if (root == null) { modelUnreadable = true; return; }

        try
        {
            modelBytes = File.ReadAllBytes(Path.Combine(root,
                models[index].File.Replace('/', Path.DirectorySeparatorChar)));
            parts = ModelPartReader.Read(modelBytes);
            modelUnreadable = parts == null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] parts: could not read {0}", models[index].File);
            modelUnreadable = true;
        }
        silhouette.Forget(SilhouetteKey);
    }

    private string SilhouetteKey => modDir + "|" + (modelIndex >= 0 ? models[modelIndex].File : "");

    private string? ModRoot()
    {
        var root = penumbra.GetModDirectory();
        return root == null || modDir == null ? null : Path.Combine(root, modDir);
    }

    // ── the part list ───────────────────────────────────────────────────────

    private void DrawParts()
    {
        var ps = Strings.Parts;
        var model = parts!;

        foreach (var (label, count) in model.ShatteredSubmeshes)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(string.Format(ps.ShatteredFmt, label, count));
            ImGui.PopTextWrapPos();
        }

        float rowHeight = ProteusStyle.S(34f);
        using var table = ImRaii.Table("##parts", 4,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, ProteusStyle.S(300f)));
        if (!table) return;

        ImGui.TableSetupColumn("Pic", ImGuiTableColumnFlags.WidthFixed, rowHeight * 2f);
        ImGui.TableSetupColumn("Part", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(90f));
        ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(80f));

        foreach (var part in model.Parts)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            silhouette.Draw(SilhouetteKey, model, part, rowHeight);

            ImGui.TableNextColumn();
            // An island is drawn under its submesh, indented, because it IS part of it — ticking both would
            // ask for the same triangles twice.
            if (part.Island >= 0) ImGui.Indent(ProteusStyle.S(12f));
            bool on = ticked.Contains(part.Label);
            using (ImRaii.Disabled(!part.Toggleable))
                if (ImGui.Checkbox($"{part.Label}##p_{part.Label}", ref on))
                {
                    if (on) ticked.Add(part.Label); else ticked.Remove(part.Label);
                    if (isolating) PushIsolate();
                }
            if (part.Island >= 0) ImGui.Unindent(ProteusStyle.S(12f));

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!part.Toggleable ? ps.AlreadyGatedTip
                               : part.Island >= 0 ? ps.IslandsTip
                               : part.Label);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(Path.GetFileName(part.Material.TrimStart('/')));

            ImGui.TableNextColumn();
            ImGui.TextDisabled(string.Format(ps.TrianglesFmt, part.TriangleCount));
        }
    }

    // ── isolate + staging ───────────────────────────────────────────────────

    private void DrawStaging()
    {
        var ps = Strings.Parts;
        var free = ModelPartReader.FreeLetters(parts!.AttributeNames);

        ImGui.TextDisabled(string.Format(ps.SelectedFmt, ticked.Count));
        ImGui.SameLine();

        using (ProteusStyle.Selected(isolating))
            if (ImGui.Button(isolating ? ps.IsolatingBtn : ps.IsolateBtn))
            {
                isolating = !isolating;
                if (isolating) PushIsolate(); else ClearIsolate();
            }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.IsolateTip);

        ImGui.Spacing();
        int left = free.Count - pending.Count;
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
                if (isolating) PushIsolate();
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
        if (existing is not { Toggles.Count: > 0 } record) return;

        ProteusStyle.SectionHeader(ps.ExistingHeader);
        ImGui.TextDisabled(string.Join(", ", record.Toggles.Keys));
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

        // The preview redirects this very model's game path, and the file behind that redirect is about to
        // be replaced. Clearing first means the character reloads from the mod's real, edited model.
        ClearIsolate();

        var byLabel = parts.Parts.ToDictionary(p => p.Label, StringComparer.Ordinal);
        var plans = pending
            .Select(t => new MeshToggleService.Plan(
                t.Name, t.Parts.Where(byLabel.ContainsKey).Select(l => byLabel[l]).ToList()))
            .Where(p => p.Parts.Count > 0)
            .ToList();

        var result = MeshToggleService.Write(
            root, models[modelIndex], parts, plans, models,
            gamePath => textureLoader.LoadRawFile(null, gamePath));

        statusIsError = !result.Ok;
        if (!result.Ok)
        {
            status = result.Message;
            log.Warning("[Proteus] parts: {0}", result.Message);
            return;
        }

        status = string.Format(ps.WrittenFmt, plans.Count, MeshToggleService.DefaultGroupName);
        if (result.Skipped.Count > 0) status += "\n" + string.Format(ps.SkippedFmt, result.Skipped.Count);

        pending.Clear();
        ticked.Clear();
        AfterModChange(root);
    }

    private void Revert()
    {
        if (ModRoot() is not { } root) return;
        ClearIsolate();

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
    /// The model on disk has changed, so the part list, the thumbnails and the record are all describing a
    /// file that no longer exists in that form — and Penumbra is still serving the old one until it is told
    /// otherwise.
    /// </summary>
    private void AfterModChange(string root)
    {
        existing = MeshToggleService.ReadRecord(root);
        silhouette.Forget(SilhouetteKey);
        if (modelIndex >= 0) SelectModel(modelIndex);

        if (modDir != null) penumbra.ReloadModDirectory(modDir);
        compositor.TriggerRecomposite("parts-written");
    }

    /// <summary>
    /// Publish a copy of the model showing only the ticked parts. Transient — see
    /// <see cref="CompositorService.SetPartPreview"/>; the mod's own files are never touched.
    /// </summary>
    private void PushIsolate()
    {
        if (modelBytes == null || parts == null || modelIndex < 0) return;
        var keep = parts.Parts.Where(p => ticked.Contains(p.Label)).ToList();
        var isolated = ModelPartWriter.Isolate(modelBytes, keep);
        if (isolated == null) return;
        compositor.SetPartPreview(models[modelIndex].GamePath, isolated);
    }

    private void ClearIsolate()
    {
        isolating = false;
        compositor.SetPartPreview(null, null);
    }
}
