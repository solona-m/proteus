using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The named-looks section at the foot of a mod's colour editor: Save, a picker for what to wear, and
/// the actions for whichever preset is on.
/// <para/>
/// This began as a row of chips, on the reasoning that seeing every look at once is what helps someone
/// FIND one worth wearing. In practice a handful of presets wrapped that row into a block several lines
/// deep, sitting above a panel where every other control is one line — so it became a combo, and the
/// section moved below Advanced and collapsed by default. The worn preset's name rides in the collapsing
/// header instead, which is the part that actually needed to be visible at a glance.
/// </summary>
public class PresetBar
{
    private readonly PresetService presets;
    private readonly PenumbraBridge penumbra;
    private readonly FileDialogManager fileDialog;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>The inline text field that is open, if any — there is at most one at a time, and opening
    /// another closes the first.</summary>
    private enum Field { None, Save, Rename }

    private Field field = Field.None;
    private string fieldText = string.Empty;
    private string fieldMod = string.Empty;

    /// <summary>Set when a field is opened, consumed on its first draw. IsWindowAppearing is no use here:
    /// the colour window is already open when the field appears inside it, so it never fires.</summary>
    private bool fieldWantsFocus;

    /// <summary>Which preset a rename is aimed at, captured when the pencil is clicked. The picker can
    /// move underneath an open field — the wearer can switch presets while typing — and renaming
    /// whatever happens to be worn at the moment they press Enter is not what they asked for.</summary>
    private Guid renameTarget;

    /// <summary>A preset waiting to be accepted from the clipboard or a file, with the mod it is staged
    /// for. Staged rather than added outright so an import into the wrong mod can be seen before it lands.</summary>
    private ModPreset? staged;
    private string stagedMod = string.Empty;

    /// <summary>The last thing worth telling the wearer — a partial apply, a bad paste, an export path.
    /// Cleared as soon as they do anything else, so it never becomes furniture.</summary>
    private string? notice;
    private bool noticeIsWarning;
    private string noticeMod = string.Empty;

    public PresetBar(PresetService presets, PenumbraBridge penumbra, FileDialogManager fileDialog,
        Configuration config, IPluginLog log)
    {
        this.presets    = presets;
        this.penumbra   = penumbra;
        this.fileDialog = fileDialog;
        this.config     = config;
        this.log        = log;
    }

    public void Draw(OverlayEntry entry)
    {
        var ps = Strings.Presets;

        // ListFor, not PresetsFor: this runs every frame and only needs names and ids, where PresetsFor
        // deep-clones every colour table it can find to hand back editable copies.
        var all       = presets.ListFor(entry);
        var appliedId = presets.AppliedIdFor(entry.ModDirectory);

        // The worn preset's name rides in the header text so a collapsed section still says which look
        // is on. "###" pins the id to a constant, because the visible half changes every time the wearer
        // switches preset and an id that moved with it would reset the open/closed state each time.
        var worn   = appliedId is { } a ? all.FirstOrDefault(p => p.Id == a) : null;
        var header = (worn == null ? ps.Header : string.Format(ps.HeaderAppliedFmt, worn.Name))
                   + "###proteusPresets";

        if (!ImGui.CollapsingHeader(header)) return;

        var collId = penumbra.GetPlayerCollectionId();

        // Deferred exactly as the Bindings tab defers its row actions: every one of these mutates the
        // list the chips were drawn from, and acting inside the loop invalidates it mid-frame.
        Guid? toApply  = null;
        Guid? toDelete = null;
        Guid? toFork   = null;
        bool  clearPin = false;

        using (ImRaii.PushId($"presets_{entry.ModDirectory}"))
        {
            // ── Save, then the picker ───────────────────────────────────────────
            //
            // Save leads because it is the one thing here that CREATES something; everything to its right
            // operates on what already exists. The picker is a combo rather than a row of chips because
            // the row could not stay on one line — a handful of presets wrapped it into a block several
            // rows deep, above a panel where every other control is a single line.
            using (ImRaii.Disabled(collId == null))
                if (ImGui.Button(ps.SaveNew, new Vector2(ProteusStyle.S(96f), 0f)))
                {
                    field     = field == Field.Save ? Field.None : Field.Save;
                    fieldMod  = entry.ModDirectory;
                    fieldText = SuggestName(all, ps);
                    fieldWantsFocus = true;
                    ClearNotice();
                }
            ProteusStyle.ReasonTooltip(collId == null ? ps.NoCollection : ps.SaveNewTip);

            ImGui.SameLine();

            var modified = worn != null && collId is { } c && presets.IsModified(entry, c);
            var preview  = worn == null
                ? ps.NoPreset
                : (worn.Source == PresetSource.Pack ? ps.PackMarker : string.Empty)
                  + worn.Name + (modified ? " " + ps.ModifiedMarker : string.Empty);

            ImGui.SetNextItemWidth(ProteusStyle.S(220f));
            if (ImGui.BeginCombo("##presetPick", preview))
            {
                if (ImGui.Selectable(ps.NoPreset, worn == null)) clearPin = true;
                ProteusStyle.ReasonTooltip(ps.NoPresetTip);

                if (all.Count > 0) ImGui.Separator();

                for (var i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    using var _ = ImRaii.PushId(i);
                    var label = (p.Source == PresetSource.Pack ? ps.PackMarker : string.Empty) + p.Name;
                    if (ImGui.Selectable(label, p.Id == appliedId)) toApply = p.Id;
                    ProteusStyle.ReasonTooltip(TipFor(p, ps));
                }
                ImGui.EndCombo();
            }

            // ── Inline name field ───────────────────────────────────────────────
            if (field != Field.None && fieldMod == entry.ModDirectory)
                DrawNameField(entry, collId, ps);

            // ── Actions for the worn preset ─────────────────────────────────────
            //
            // The worn one, full stop. With chips there was a difference between "selected" and "worn"
            // and a second piece of state to keep them straight; picking from a combo wears what you
            // pick, so the distinction had nowhere left to live.
            if (worn != null && field == Field.None)
                DrawActions(entry, worn, ps, ref toDelete, ref toFork);

            // ── Import ──────────────────────────────────────────────────────────
            if (staged != null && stagedMod == entry.ModDirectory) DrawStaged(entry, ps);
            else                                                   DrawImportRow(entry, ps);

            if (notice != null && noticeMod == entry.ModDirectory)
            {
                if (!noticeIsWarning) ProteusStyle.DisabledWrapped(notice);
                else
                {
                    // Wrapped by hand: TextColored does not wrap, and every warning here is a full
                    // sentence that runs a third longer once translated.
                    ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - ProteusStyle.S(14f));
                    ImGui.TextColored(ProteusStyle.Warn, notice);
                    ImGui.PopTextWrapPos();
                }
            }
        }

        // ── Deferred mutations ──────────────────────────────────────────────────
        if (clearPin)
        {
            presets.ClearApplied(entry.ModDirectory);
        }
        else if (toApply is { } applyId && collId is { } applyColl
                 && presets.Get(entry, applyId) is { } toWear)
        {
            var report = presets.Apply(entry, applyColl, toWear);
            ReportApply(entry, report, toWear, Strings.Presets);
        }
        else if (toFork is { } forkId && presets.Get(entry, forkId) is { } source)
        {
            // Forking wears the copy straight away, which is also what puts its action row on screen.
            var fork = presets.Add(entry.ModDirectory, source);
            if (collId is { } forkColl) presets.Apply(entry, forkColl, fork);
        }
        else if (toDelete is { } id)
        {
            presets.Delete(entry, id);
        }
    }

    // ── Pieces ──────────────────────────────────────────────────────────────────

    private void DrawNameField(OverlayEntry entry, Guid? collId, PresetsStrings ps)
    {
        // Before the widget, not after: SetKeyboardFocusHere targets the NEXT item submitted.
        if (fieldWantsFocus)
        {
            ImGui.SetKeyboardFocusHere();
            fieldWantsFocus = false;
        }

        ImGui.SetNextItemWidth(ProteusStyle.S(220f));
        var enter = ImGui.InputText("##presetName", ref fieldText, 64,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        var ok = !string.IsNullOrWhiteSpace(fieldText);
        ImGui.SameLine();
        using (ImRaii.Disabled(!ok))
            if (ImGui.Button(field == Field.Save ? ps.Save : ps.RenameConfirm) || (enter && ok))
            {
                // Save wears what it saves, so the picker and the action row both follow on their own.
                if (field == Field.Save && collId is { } c)
                    presets.Save(entry, c, fieldText.Trim());
                else if (field == Field.Rename && renameTarget != Guid.Empty)
                    presets.Rename(entry.ModDirectory, renameTarget, fieldText.Trim());
                field = Field.None;
            }
        ProteusStyle.ReasonTooltip(ok ? null : ps.NeedsAName);

        ImGui.SameLine();
        if (ImGui.Button(ps.Cancel)) field = Field.None;
    }

    private void DrawActions(OverlayEntry entry, PresetService.PresetInfo sel, PresetsStrings ps,
        ref Guid? toDelete, ref Guid? toFork)
    {
        var collId   = penumbra.GetPlayerCollectionId();
        var isPack   = sel.Source == PresetSource.Pack;
        var pinned   = presets.AppliedIdFor(entry.ModDirectory) == sel.Id;
        var modified = pinned && collId is { } c && presets.IsModified(entry, c);

        ImGui.TextDisabled(string.Format(ps.SelectedFmt, sel.Name,
            isPack ? ps.FromPack : Ago(sel.LastEditUtc, ps)));

        // Update — only meaningful on a preset of one's own that has actually drifted. A pack preset is
        // read-only, so the same gesture forks it instead, which is what the tooltip says.
        ImGui.SameLine(0f, ProteusStyle.S(16f));
        using (ImRaii.Disabled(isPack || !modified))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, ps.Update) && collId is { } uc)
                presets.Update(entry, uc, sel.Id);
        ProteusStyle.ReasonTooltip(isPack ? ps.PackReadOnly : modified ? ps.UpdateTip : ps.NothingChanged);

        ImGui.SameLine();
        using (ImRaii.Disabled(isPack))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Pen))
            {
                field       = Field.Rename;
                fieldMod    = entry.ModDirectory;
                fieldText   = sel.Name;
                renameTarget = sel.Id;
                fieldWantsFocus = true;
            }
        ProteusStyle.ReasonTooltip(isPack ? ps.PackReadOnly : ps.Rename);

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Clone)) toFork = sel.Id;
        ProteusStyle.ReasonTooltip(isPack ? ps.ForkTip : ps.Duplicate);

        // Copy and Export are the two paths that need the preset's actual contents, so they fetch the
        // full copy on the click rather than the listing holding one for every chip every frame.
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy) && presets.Get(entry, sel.Id) is { } toCopy)
        {
            ImGui.SetClipboardText(PresetCodec.ToShareCode(toCopy));
            SetNotice(entry, ps.CodeCopied, warning: false);
        }
        ProteusStyle.ReasonTooltip(ps.CopyCodeTip);

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.FileExport) && presets.Get(entry, sel.Id) is { } toSave)
            BrowseForExport(entry, toSave, ps);
        ProteusStyle.ReasonTooltip(ps.ExportTip);

        // Delete last, and Ctrl-armed. The house style for anything destructive — there are no
        // confirmation modals anywhere in this plugin, and this is not the place to introduce the first.
        ImGui.SameLine();
        var armed = !isPack && ImGui.GetIO().KeyCtrl;
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, armed ? 1f : 0.5f))
        using (ImRaii.Disabled(!armed))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash)) toDelete = sel.Id;
        ProteusStyle.ReasonTooltip(isPack ? ps.PackReadOnly : ps.DeleteTip);
    }

    private void DrawImportRow(OverlayEntry entry, PresetsStrings ps)
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Paste, ps.PasteCode))
        {
            var result = PresetCodec.FromShareCode(SafeClipboard());
            if (result.Preset == null) SetNotice(entry, result.Error!, warning: true);
            else Stage(entry, result.Preset);
        }
        ProteusStyle.ReasonTooltip(ps.PasteCodeTip);

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FileImport, ps.Import)) BrowseForImport(entry, ps);
        ProteusStyle.ReasonTooltip(ps.ImportTip);
    }

    /// <summary>
    /// The staged import, with its origin spelled out. A preset made for another mod is not refused —
    /// the option and colour names might well line up, and refusing outright would block the legitimate
    /// case of a pack republished under a new name — but it is never added without saying so first.
    /// </summary>
    private void DrawStaged(OverlayEntry entry, PresetsStrings ps)
    {
        var p = staged!;
        var sameMod = string.IsNullOrEmpty(p.ModName)
                   || string.Equals(p.ModName, entry.ModName, StringComparison.OrdinalIgnoreCase);

        using (ProteusStyle.Card(sameMod ? null : ProteusStyle.Warn))
        {
            ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - ProteusStyle.S(14f));
            if (sameMod)
                ImGui.TextUnformatted(string.Format(ps.StagedFmt, p.Name));
            else
                ImGui.TextColored(ProteusStyle.Warn,
                    string.Format(ps.StagedOtherModFmt, p.Name, p.ModName, p.ModAuthor ?? "?", entry.ModName));
            ImGui.PopTextWrapPos();

            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, ps.AddStaged))
            {
                // Added, not worn: an import should not change how you look without being asked. It is
                // in the picker straight away for whenever they want it.
                var added = presets.Add(entry.ModDirectory, p);
                staged = null;
                SetNotice(entry, string.Format(ps.AddedFmt, added.Name), warning: false);
            }

            ImGui.SameLine();
            if (ImGui.Button(ps.Discard)) staged = null;
        }
    }

    // ── File dialogs ────────────────────────────────────────────────────────────

    private void BrowseForExport(OverlayEntry entry, ModPreset preset, PresetsStrings ps)
    {
        fileDialog.SaveFileDialog(
            ps.ExportDialogTitle, ps.DialogFilter + "{" + PresetCodec.FileExtension + "}",
            PresetCodec.SuggestedFileName(preset), PresetCodec.FileExtension,
            (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                RememberDirectory(path);
                try
                {
                    PresetCodec.ToFile(preset, path);
                    SetNotice(entry, string.Format(ps.ExportedFmt, Path.GetFileName(path)), warning: false);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[Proteus] preset: export failed");
                    SetNotice(entry, string.Format(ps.ExportFailedFmt, ex.Message), warning: true);
                }
            },
            StartDirectory());
    }

    private void BrowseForImport(OverlayEntry entry, PresetsStrings ps)
    {
        fileDialog.OpenFileDialog(
            ps.ImportDialogTitle, ps.DialogFilter + "{" + PresetCodec.FileExtension + "}",
            (ok, paths) =>
            {
                var path = ok ? paths.FirstOrDefault() : null;
                if (string.IsNullOrEmpty(path)) return;
                RememberDirectory(path);

                var result = PresetCodec.FromFile(path);
                if (result.Preset == null) SetNotice(entry, result.Error!, warning: true);
                else Stage(entry, result.Preset);
            },
            1, StartDirectory());
    }

    /// <summary>Where a preset dialog opens: wherever the last export went, else the desktop. Shared with
    /// the mod exporter deliberately — someone who keeps their Proteus files in one folder keeps both
    /// kinds there.</summary>
    private string? StartDirectory()
    {
        var last = config.LastExportDirectory;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;

        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Desktop })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
        }
        return null;
    }

    private void RememberDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)
            || string.Equals(dir, config.LastExportDirectory, StringComparison.OrdinalIgnoreCase)) return;
        config.LastExportDirectory = dir;
        config.Save();
    }

    // ── Small helpers ───────────────────────────────────────────────────────────

    private void Stage(OverlayEntry entry, ModPreset preset)
    {
        staged    = preset;
        stagedMod = entry.ModDirectory;
        ClearNotice();
    }

    /// <summary>ImGui's clipboard read throws on some platforms when the clipboard holds a non-text
    /// payload — an image copied from a browser is enough. A failed paste must not take the window down.</summary>
    private string? SafeClipboard()
    {
        try { return ImGui.GetClipboardText(); }
        catch (Exception ex) { log.Debug(ex, "[Proteus] preset: could not read the clipboard"); return null; }
    }

    private void ReportApply(OverlayEntry entry, PresetApplyReport report, ModPreset preset,
        PresetsStrings ps)
    {
        if (report.FullyApplied) { ClearNotice(); return; }

        var parts = new List<string>();
        if (report.MissingGroups.Count > 0)
            parts.Add(string.Format(ps.MissingGroupsFmt, string.Join(", ", report.MissingGroups)));
        if (report.MissingOptions.Count > 0)
            parts.Add(string.Format(ps.MissingOptionsFmt,
                string.Join(", ", report.MissingOptions.Select(x => $"{x.Group}/{x.Option}"))));

        SetNotice(entry, string.Format(ps.PartialApplyFmt, preset.Name, string.Join(" ", parts)), warning: true);
    }

    private void SetNotice(OverlayEntry entry, string text, bool warning)
    {
        notice          = text;
        noticeIsWarning = warning;
        noticeMod       = entry.ModDirectory;
    }

    private void ClearNotice() => notice = null;

    private static string SuggestName(List<PresetService.PresetInfo> existing, PresetsStrings ps)
        => existing.Count == 0 ? ps.FirstPresetName : string.Format(ps.NthPresetNameFmt, existing.Count + 1);

    private static string TipFor(PresetService.PresetInfo p, PresetsStrings ps)
    {
        var origin = p.Source == PresetSource.Pack ? ps.FromPack : Ago(p.LastEditUtc, ps);
        return string.IsNullOrWhiteSpace(p.Description) ? $"{p.Name} — {origin}"
                                                        : $"{p.Name} — {origin}\n{p.Description}";
    }

    private static string Ago(DateTime utc, PresetsStrings ps)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return ps.JustNow;
        if (span.TotalHours   < 1) return string.Format(ps.MinutesAgoFmt, (int)span.TotalMinutes);
        if (span.TotalDays    < 1) return string.Format(ps.HoursAgoFmt,   (int)span.TotalHours);
        return string.Format(ps.DaysAgoFmt, (int)span.TotalDays);
    }
}
