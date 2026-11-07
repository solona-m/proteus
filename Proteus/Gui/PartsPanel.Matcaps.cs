using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;

namespace Proteus.Gui;

/// <summary>
/// The Matcaps tool's wiring: the pane's context, and Apply, Change, Undo and Forget. The heavy half of each runs on a
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
            Path.Combine(Path.GetTempPath(), "Proteus", "matcap"));

    private void RescanMatcaps(bool force)
    {
        if (matcapScan is { IsCompleted: false }) return;
        if (penumbra.GetAllModsQuiet() is not { } all || penumbra.GetModDirectory() is not { } root) return;
        matcapIndex ??= new MatcapIndex(null);
        var index = matcapIndex;
        matcapScan = Task.Run(() => index.Refresh(all, root, force));
    }

    private void DrawMatcaps()
    {
        FinishMatcapJob();
        var root = vanilla == null ? ModRoot() : null;
        if (matcapIndex?.Current == null && matcapScan == null) RescanMatcaps(false);

        // Read the record and its states when the open mod changes or after a write — never every frame, and never a write.
        var key = root ?? "";
        if (matcapRecordFor != key)
        {
            matcapRecord = root != null ? MatcapRecord.Read(root) : new MatcapRecord();
            matcapStates = root != null ? MatcapWriter.Detect(root) : new Dictionary<string, MatcapWriter.State>();
            matcapRecordFor = key;
        }
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
        var chosen = parts?.Parts.Where(p => ticked.Contains(p.Label)).ToList() ?? [];
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
            Change = (name, choice) => StartMatcapChange(name, choice),
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
        });
    }

    private void StartMatcapApply(MatcapChoice choice, bool confirmEnable)
    {
        matcapError = null;
        if (matcapJob is { IsCompleted: false } || parts == null || modelIndex < 0) return;
        // A pending brush stroke rewrites the whole model from its opening bytes: save it first, or stop.
        if (!FlushPending()) return;   // the save says why in the status line
        var chosen = parts.Parts.Where(p => ticked.Contains(p.Label)).ToList();
        var env = MatcapEnv();

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
            return new MatcapJobResult(root, outcome, outcome.Ok && choice.IsLibrary ? choice : null, confirmEnable,
                                       prepared.SiblingsSkipped, prepared.Lod0Cut, prepared.AlphaTested, model.GamePath) { IsApply = true };
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

    private void StartMatcapChange(string materialName, MatcapChoice choice)
    {
        matcapError = null;
        if (matcapJob is { IsCompleted: false } || ModRoot() is not { } root) return;
        var env = MatcapEnv();
        var gamePath = modelIndex >= 0 && modelIndex < models.Count ? models[modelIndex].GamePath : null;
        matcapJob = Task.Run(() =>
        {
            var (src, mtrl, temp, code, msg) = MatcapStudio.PrepareChange(root, materialName, choice, env);
            var outcome = src == null ? MatcapWriter.Outcome.Refuse(code, msg) : MatcapWriter.Change(root, materialName, src, mtrl, temp);
            return new MatcapJobResult(root, outcome, outcome.Ok && choice.IsLibrary ? choice : null, true, [], false, false, gamePath) { IsChange = true };
        });
    }

    private void StartMatcapUndo(bool forget)
    {
        matcapError = null;
        if (matcapJob is { IsCompleted: false } || ModRoot() is not { } root || modelIndex < 0) return;
        if (!FlushPending()) return;
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
                                   prepared?.SiblingsSkipped ?? [], prepared?.Lod0Cut ?? false, prepared?.AlphaTested ?? false, piece.GamePath) { IsApply = true });
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
            return;
        }

        var lines = new List<string> { r.Outcome.Message };
        if (r.Select is { Library: { } lib, Option: { } option })
        {
            var sel = MatcapSelection.Select(new PenumbraMatcapAdapter(penumbra), lib, option, r.ConfirmEnable);
            if (!sel.Ok) lines.Add(sel.Message);
            else if (!sel.Winning && sel.WinningFile != null) lines.Add(string.Format(s.NotWinningFmt, sel.WinningFile));
            matcapPanel.InvalidateStatus();
        }
        if (r.Lod0Cut) lines.Add(s.Lod0Cut);
        if (r.SiblingsSkipped.Count > 0) lines.Add(string.Format(s.SiblingsSkippedFmt, string.Join(", ", r.SiblingsSkipped)));
        if (r.AlphaTested) lines.Add(s.AlphaWarn);
        if (r.IsApply && r.GamePath != null && DrawnElsewhere(r.Root, r.GamePath, r.Outcome.Files) is { } drawn) lines.Add(drawn);
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

        status = string.Join("\n", lines.Where(l => l.Length > 0));
        statusIsError = false;
        matcapError = null;
        ticked.Clear();
        log.Information("[Proteus] matcap: {0}", status.Replace('\n', ' '));
        AfterModChange(r.Root, r.Outcome.Files.Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>The warning when the character draws another file for this game path than the ones written.</summary>
    private string? DrawnElsewhere(string root, string gamePath, IReadOnlyList<string> written)
    {
        if (penumbra.ResolvePlayerPaths([gamePath]) is not { } resolved || !resolved.TryGetValue(gamePath, out var file) || file == null)
            return null;
        return MatcapPanelModel.DrawnStatus(root, gamePath, written, file, penumbra.GetModDirectory() ?? "", compositor.FaceUpstreamPath(gamePath));
    }
}
