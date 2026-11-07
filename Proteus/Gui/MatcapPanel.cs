using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;

namespace Proteus.Gui;

/// <summary>
/// The Matcaps tool's settings pane in the Studio: which matcap (your images or a library's options, with thumbnails),
/// Apply, and the matcaps already on the open model with Change, Forget and Undo. It holds only what the user picked;
/// everything it decides comes from <see cref="MatcapPanelModel"/>, and everything it does is a callback the Studio
/// wires (<see cref="Context"/>).
/// </summary>
internal sealed class MatcapPanel
{
    /// <summary>What the pane shows and what its buttons do, filled by the Studio each frame.</summary>
    public sealed class Context
    {
        public required bool Penumbra;
        public required bool Busy;
        public required IReadOnlyList<ModelPart> Ticked;
        public required int MaterialRoom;
        public required IReadOnlyList<MatcapImage> Images;
        public required string? ImageFolder;
        public required MatcapIndex.State? Libraries;
        public required bool Scanning;
        public required string? ModsRoot;
        public required MatcapRecord Record;
        public required IReadOnlyDictionary<string, MatcapWriter.State> States;
        /// <summary>The matcaps drawn in the open model.</summary>
        public required IReadOnlyList<MatcapRecord.MaterialEntry> Entries;
        /// <summary>The other files of the open model's apply group: an Undo here restores them too.</summary>
        public required IReadOnlyList<string> GroupFiles;
        public required Func<MatcapLibrary, MatcapSelection.Status> LibraryStatus;
        public required Action<MatcapChoice, bool> Apply;
        public required Action<string, MatcapChoice> Change;
        public required Action Undo;
        public required Action Forget;
        public required Action Rescan;
        public required Action RefreshImages;
        public string? Error;
        /// <summary>The open model is a face: only its horns take a matcap.</summary>
        public bool FaceTarget;
        /// <summary>For a face: it has horn parts at all.</summary>
        public bool HasHorns = true;
        /// <summary>Proteus's own matcaps on disk (see <see cref="IMatcapPack"/>).</summary>
        public IReadOnlyList<MatcapImage> PackImages = [];
        /// <summary>Their download, or null when there is no pack.</summary>
        public MatcapPackStatus? PackStatus;
        /// <summary>Try the download again after a failure.</summary>
        public Action? RetryPack;
    }

    /// <summary>Where the matcap is picked from.</summary>
    internal enum SourceKind { Images, Proteus, Libraries }

    private SourceKind source;
    private string? image;
    private string? packImage;
    private string? libraryKey;
    private string? option;
    private bool confirmOpen;
    private MatcapSelection.Status? cachedStatus;
    private string? cachedStatusKey;

    /// <summary>The library status is IPC: read when the choice changes or after a write, not every frame.</summary>
    public void InvalidateStatus() => cachedStatusKey = null;

    /// <summary>The current choice, or null.</summary>
    public MatcapChoice? Choice(Context ctx)
    {
        if (source == SourceKind.Images)
            return image != null && ctx.Images.FirstOrDefault(i => i.Name == image) is { } img ? MatcapChoice.OfImage(img) : null;
        if (source == SourceKind.Proteus)
            return packImage != null && ctx.PackImages.FirstOrDefault(i => i.Name == packImage) is { } p ? MatcapChoice.OfImage(p) : null;
        var lib = SelectedLibrary(ctx);
        return lib != null && option != null && lib.Options.Any(o => o.Name == option) ? MatcapChoice.OfLibrary(lib, option) : null;
    }

    private MatcapLibrary? SelectedLibrary(Context ctx) =>
        ctx.Libraries?.Libraries.FirstOrDefault(l => Key(l) == libraryKey);

    private static string Key(MatcapLibrary l) => l.Mod + "|" + l.Group;

    public void Draw(Context ctx)
    {
        var s = Strings.Matcap;
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(s.Guide);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.RadioButton(s.SourceImages, source == SourceKind.Images)) source = SourceKind.Images;
        ImGui.SameLine();
        if (ImGui.RadioButton(s.SourceProteus, source == SourceKind.Proteus)) source = SourceKind.Proteus;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.SourceProteusTip);
        ImGui.SameLine();
        if (ImGui.RadioButton(s.SourceLibraries, source == SourceKind.Libraries)) { source = SourceKind.Libraries; InvalidateStatus(); }
        ImGui.Separator();

        switch (source)
        {
            case SourceKind.Libraries: DrawLibraries(ctx); break;
            case SourceKind.Proteus: DrawProteus(ctx); break;
            default: DrawImages(ctx); break;
        }

        ImGui.Separator();
        DrawApply(ctx);
        DrawExisting(ctx);
    }

    private void DrawImages(Context ctx)
    {
        var s = Strings.Matcap;
        if (ImGui.Button(s.OpenFolder) && ctx.ImageFolder != null)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ctx.ImageFolder) { UseShellExecute = true }); }
            catch { /* no file manager: the path is in the note */ }
        ImGui.SameLine();
        if (ImGui.Button(s.Refresh)) ctx.RefreshImages();

        if (ctx.Images.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(MatcapPanelModel.NoImages(ctx.ImageFolder));
            ImGui.PopTextWrapPos();
            return;
        }
        DrawGrid(ctx.Images.Select(i => (i.Name, i.Path, i.FromMod ? $"{i.Name} {s.ThisMod}" : i.Name)).ToList(),
                 image, picked => image = picked);
    }

    /// <summary>Picks <paramref name="name"/> from <paramref name="kind"/>, as a click would: the tests' way in.</summary>
    internal void Pick(SourceKind kind, string name)
    {
        source = kind;
        if (kind == SourceKind.Proteus) packImage = name; else if (kind == SourceKind.Images) image = name;
    }

    private void DrawProteus(Context ctx)
    {
        var s = Strings.Matcap;
        var st = ctx.PackStatus;
        if (st != null && MatcapPanelModel.PackLine(st, ctx.PackImages.Count) is { } line)
        {
            ImGui.PushTextWrapPos(0f);
            if (st.State == MatcapPackState.Failed) ImGui.TextColored(ProteusStyle.Warn, line); else ImGui.TextDisabled(line);
            ImGui.PopTextWrapPos();
            if (st.State == MatcapPackState.Failed && st.Detail.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(st.Detail);
            if (st.State == MatcapPackState.Failed && ImGui.Button(s.PackRetry)) ctx.RetryPack?.Invoke();
        }
        if (ctx.PackImages.Count == 0) return;
        DrawGrid(ctx.PackImages.Select(i => (i.Name, i.Path, i.Name)).ToList(), packImage, picked => packImage = picked);
    }

    private void DrawLibraries(Context ctx)
    {
        var s = Strings.Matcap;
        if (ImGui.Button(s.Rescan)) { ctx.Rescan(); InvalidateStatus(); }
        if (ctx.Scanning) { ImGui.SameLine(); ImGui.TextDisabled(s.Scanning); }
        var libs = ctx.Libraries?.Libraries ?? [];
        if (libs.Count == 0)
        {
            if (!ctx.Scanning)
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextDisabled(s.NoLibraries);
                ImGui.PopTextWrapPos();
            }
            return;
        }

        var lib = SelectedLibrary(ctx);
        var status = lib != null ? Status(ctx, lib) : null;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##matcapLib", lib == null ? "…" : lib.Label + (status is { Enabled: false } ? " " + s.LibraryOffTag : "")))
        {
            foreach (var l in libs)
                if (ImGui.Selectable(l.Label + "##" + Key(l), Key(l) == libraryKey))
                {
                    libraryKey = Key(l);
                    option = null;
                    InvalidateStatus();
                }
            ImGui.EndCombo();
        }
        if (lib == null) return;
        status ??= Status(ctx, lib);
        if (option == null && status.CurrentOption is { } cur && lib.Options.Any(o => o.Name == cur)) option = cur;

        var root = ctx.ModsRoot ?? "";
        DrawGrid(lib.Options.Select(o => (o.Name, Path.Combine(root, lib.Mod, o.File.Replace('\\', Path.DirectorySeparatorChar)),
                                          o.Name == status.CurrentOption ? $"{o.Name} ✓" : o.Name)).ToList(),
                 option, picked => option = picked);

        ImGui.PushTextWrapPos(0f);
        foreach (var w in MatcapPanelModel.LibraryWarnings(lib, status, ctx.Record, ctx.Libraries!.ProvidersOf(lib.Texture)))
            ImGui.TextColored(ProteusStyle.Warn, w);
        ImGui.PopTextWrapPos();
    }

    private MatcapSelection.Status Status(Context ctx, MatcapLibrary lib)
    {
        if (cachedStatus == null || cachedStatusKey != Key(lib))
        {
            cachedStatus = ctx.LibraryStatus(lib);
            cachedStatusKey = Key(lib);
        }
        return cachedStatus;
    }

    /// <summary>A wrapping grid of thumbnail buttons; a failed thumbnail falls back to the name.</summary>
    private static void DrawGrid(List<(string Name, string Path, string Tip)> items, string? selected, Action<string> pick)
    {
        float size = ProteusStyle.S(56f);
        float avail = ImGui.GetContentRegionAvail().X;
        float x = 0f, gap = ImGui.GetStyle().ItemSpacing.X;
        foreach (var (name, path, tip) in items)
        {
            if (x > 0f && x + size > avail) x = 0f; else if (x > 0f) ImGui.SameLine();
            bool clicked;
            bool hasThumb = false;
            using (ProteusStyle.Selected(name == selected))
            {
                if (ColorTableEditor.EffectThumbs is { } thumbs)
                    clicked = thumbs.DrawButton($"##mc_{name}", path, size, out hasThumb);
                else
                    clicked = false;
                if (!hasThumb)
                {
                    if (ColorTableEditor.EffectThumbs != null) ImGui.SameLine(0, 0);
                    clicked |= ImGui.Button($"{ProteusStyle.Ellipsize(name, size)}##mcn_{name}", new Vector2(size, size));
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
            if (clicked) pick(name);
            x += size + gap;
        }
    }

    private void DrawApply(Context ctx)
    {
        var s = Strings.Matcap;
        var choice = Choice(ctx);
        if (ctx.FaceTarget)
        {
            ImGui.PushTextWrapPos(0f);
            if (!ctx.HasHorns) ImGui.TextColored(ProteusStyle.Warn, s.NoHorns);
            else ImGui.TextDisabled(s.HornColourNote);
            ImGui.PopTextWrapPos();
        }
        ImGui.TextDisabled(string.Format(s.TickedFmt, ctx.Ticked.Count));
        ImGui.TextDisabled(string.Format(s.RoomFmt, Math.Max(0, ctx.MaterialRoom)));
        if (ctx.Ticked.Any(p => p.Material.Contains("hir", StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ProteusStyle.Warn, s.AlphaWarn);
            ImGui.PopTextWrapPos();
        }

        var why = MatcapPanelModel.ApplyDisabledReason(ctx.Penumbra, ctx.Ticked.Count, choice != null, ctx.MaterialRoom);
        using (ImRaii.Disabled(why != null || ctx.Busy))
            if (ImGui.Button(s.Apply, new Vector2(-1f, 0f)) && choice != null)
            {
                if (choice.Library is { } lib && cachedStatus is { Known: true, Enabled: false } && cachedStatusKey == Key(lib))
                    confirmOpen = true;
                else
                {
                    ctx.Apply(choice, false);
                    InvalidateStatus();
                }
            }
        if (why != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(why);
        if (ctx.Busy) ImGui.TextDisabled(s.Working);
        if (ctx.Error is { Length: > 0 } err)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ProteusStyle.Bad, err);
            ImGui.PopTextWrapPos();
        }

        // The confirmation, inline rather than a modal: the pane stays usable and nothing is written until Yes.
        if (confirmOpen && choice?.Library is { } l && cachedStatus is { } st)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ProteusStyle.Warn, string.Format(s.ConfirmEnableFmt, l.Name, st.Collection, MatcapSelection.AlsoText(l)));
            ImGui.PopTextWrapPos();
            if (ImGui.Button(s.ConfirmYes)) { confirmOpen = false; ctx.Apply(choice, true); InvalidateStatus(); }
            ImGui.SameLine();
            if (ImGui.Button(s.ConfirmNo)) confirmOpen = false;
        }
        else confirmOpen = false;
    }

    private void DrawExisting(Context ctx)
    {
        var s = Strings.Matcap;
        if (ctx.Entries.Count == 0) return;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader(s.ExistingHeader, ImGuiTreeNodeFlags.DefaultOpen)) return;
        var choice = Choice(ctx);
        bool anySuperseded = false;
        foreach (var entry in ctx.Entries)
        {
            var state = ctx.States.TryGetValue(entry.Name, out var st) ? st : MatcapWriter.State.Ok;
            anySuperseded |= state == MatcapWriter.State.Superseded;
            bool installed = true, enabled = true;
            if (entry.Source.Library is { } lr && ctx.Libraries is { } libs)
            {
                var lib = libs.Find(lr.Mod, lr.Group);
                installed = lib != null;
                if (lib != null) enabled = ctx.LibraryStatus(lib).Enabled;
            }
            ImGui.TextUnformatted(MatcapWriter.StemOf(entry.Name));
            ImGui.SameLine();
            ImGui.TextDisabled(MatcapPanelModel.SourceText(entry.Source));
            if (MatcapPanelModel.EntryState(entry, state, installed, enabled) is { } line)
                ImGui.TextColored(ProteusStyle.Warn, line);
            using (ImRaii.PushId(entry.Name))
            using (ImRaii.Disabled(!MatcapPanelModel.CanChange(entry, choice) || ctx.Busy || state == MatcapWriter.State.Superseded))
                if (ImGui.SmallButton(s.Change) && choice != null) { ctx.Change(entry.Name, choice); InvalidateStatus(); }
            if (choice != null && !MatcapPanelModel.CanChange(entry, choice) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(s.ChangeKindTip);
        }
        if (anySuperseded)
            using (ImRaii.Disabled(ctx.Busy))
                if (ImGui.Button(s.Forget)) ctx.Forget();
        using (ImRaii.Disabled(ctx.Busy))
            if (ImGui.Button(s.Undo)) ctx.Undo();
        if (ctx.GroupFiles.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Format(s.UndoGroupFmt, string.Join(", ", ctx.GroupFiles)));
    }
}
