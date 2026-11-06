#if PROTEUS_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Proteus.Localization;
using Proteus.Services;
using Proteus.TestHarness;

namespace Proteus.Gui;

/// <summary>
/// The Studio as the test harness drives it (harness builds only, ADR 0002): each route runs the code its control runs —
/// <see cref="SwitchTool"/>, <see cref="SwitchMoveSelect"/>, <see cref="ToggleLock"/>, the gizmo's
/// <see cref="BeginPartDrag"/> and <see cref="PlacePartDrag"/>, <see cref="FinishMove"/> and the save — on the framework
/// thread between frames, so nothing here makes an ImGui call. Put-back reverts only what is provably the harness's.
/// </summary>
public sealed partial class PartsPanel : IStudioHarness
{
    private Action? harnessShowWindow;
    private Func<bool>? harnessWindowOpen;
    private Action? harnessCloseWindow;

    /// <summary>The panel as the harness's Studio port, with the window's hooks (open on the Studio tab, is open, close).</summary>
    internal IStudioHarness AsHarnessStudio(Action showWindow, Func<bool> windowOpen, Action closeWindow)
    {
        harnessShowWindow = showWindow;
        harnessWindowOpen = windowOpen;
        harnessCloseWindow = closeWindow;
        return this;
    }

    /// <summary>The panel as it was before the harness first changed it, to put back.</summary>
    private sealed record HarnessSnapshot(string? ModDir, string? ModelFile, Tool Tool, MoveSelectMode Mode, bool ModelView,
                                          bool Mirror, bool Adjacent, bool MoveSkin, string LockKey, string[]? Locks);

    /// <summary>The per-frame state a route depends on, brought up to date: the window may be closed, and Draw not running.</summary>
    private void HarnessSync() => UpdateSkinMoves();

    private bool HarnessDragOpen => volume is { Moving: true } or { Grabbing: true } or { StrokeOpen: true };

    private string? HarnessRoot(string dir) => penumbra.GetModDirectory() is { } root ? Path.Combine(root, dir) : null;

    public bool IsOpen(string modDir, string file)
    {
        HarnessSync();
        return vanilla == null && modelIndex >= 0 && modelIndex < models.Count && parts != null
            && string.Equals(this.modDir, modDir, StringComparison.OrdinalIgnoreCase)
            && string.Equals(models[modelIndex].File, file, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, object?> View()
    {
        HarnessSync();
        var all = moveAll ? AllNow() : null;
        var pivot = PartTool ? MovePivot() : null;
        var open = vanilla == null && modelIndex >= 0 && modelIndex < models.Count && modDir != null
            ? new { modDir, file = models[modelIndex].File, gamePath = models[modelIndex].GamePath }
            : null;
        return new Dictionary<string, object?>
        {
            ["open"] = open,
            ["window"] = new { shown = harnessWindowOpen?.Invoke() ?? false },
            ["tool"] = tool switch { Tool.Move => "move", Tool.Rotate => "rotate", Tool.Scale => "scale", _ => "other" },
            ["mode"] = moveSelect switch { MoveSelectMode.Polygons => "polygons", MoveSelectMode.All => "all", _ => "parts" },
            ["view"] = showModelView ? "model" : "character",
            ["settings"] = new { mirror = mirrorBrush, adjacent = moveAdjacent, moveSkin },
            ["effective"] = new { mirrorNow = MirrorNow, adjacentNow = AdjacentNow, skinMovesNow = SkinMovesNow,
                                  followReady = volume?.FollowReady == true },
            ["parts"] = parts?.Parts.Select(p => new
            {
                label = p.Label, material = Path.GetFileName(p.Material.TrimStart('/')), island = p.Island, skin = IsSkin(p),
                locked = IsLocked(p), moves = MoveAllSelection.Moves(IsSkin(p), IsLocked(p), SkinMovesNow),
            }).ToList(),
            ["chosen"] = new { part = movePart, polygons = movePolys.Count },
            ["all"] = all is { } a
                ? new { seeds = a.Seeds.Length, movingRows = a.MovingRows, rows = a.Rows, includesSkin = a.IncludesSkin,
                        pivot = pivot is { } c ? new[] { c.X, c.Y, c.Z } : null }
                : null,
            ["pivot"] = pivot is { } at ? new[] { at.X, at.Y, at.Z } : null,
            ["help"] = MoveAllSelection.HelpFor(tool switch { Tool.Rotate => MoveHelpTool.Rotate, Tool.Scale => MoveHelpTool.Scale, _ => MoveHelpTool.Move },
                                                showModelView, moveAll).ToString(),
            ["edit"] = volume is { } v
                ? new { dirty = v.Dirty, canUndo = v.CanUndo, worstMm = v.Worst * 1000f, saveOwed = brushChangedAt >= 0, editVersion = v.EditVersion }
                : null,
            ["status"] = status is { } text ? new { text, isError = statusIsError } : null,
        };
    }

    StudioOpenOutcome IStudioHarness.Open(string modDir, string model, bool show)
    {
        HarnessSync();
        static StudioOpenOutcome Refuse(int status, string code, string message) => new(new StudioRefusal(status, code, message), null, null, false, -1, null);
        if (HarnessRoot(modDir) is not { } root) return Refuse(503, "penumbra-unavailable", "Penumbra is not available");
        // Never takes the user's work: an open drag, stroke or an unsaved edit is theirs, not flushed here.
        if (HarnessDragOpen || brushChangedAt >= 0)
            return Refuse(409, "studio-busy", "the Studio has an edit under way or not saved yet");
        if (PenumbraModMeta.IsLegacyFolder(root, waitIfHeld: false))
            return Refuse(409, "legacy-mod", "the mod is a pre-v4 Penumbra mod");
        // A revert at put-back undoes every saved Studio edit in the mod: only a mod with none is opened.
        if (MeshVolumeService.PatchedCount(root) > 0)
            return Refuse(409, "studio-has-edits", "the mod already has saved Studio edits; the harness only opens a mod with none");

        var listed = PublishedModels(PenumbraModMeta.ReadAllRedirects(root));
        listed.AddRange(ContentModels(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        var (index, error) = StudioModelMatch.Match(listed, model);
        if (error != null)
            return Refuse(error == "model-ambiguous" ? 409 : 404, error,
                          (error == "model-ambiguous" ? "two models have that game path; give the file. " : "no such model. ")
                          + "The mod's models: " + string.Join("; ", listed.Select(m => $"{m.File} ({m.GamePath})")));

        var snapshot = new HarnessSnapshot(vanilla == null ? this.modDir : null,
                                           vanilla == null && modelIndex >= 0 && modelIndex < models.Count ? models[modelIndex].File : null,
                                           tool, moveSelect, showModelView, mirrorBrush, moveAdjacent, moveSkin,
                                           modDir + "|" + listed[index].File,
                                           lockedParts.TryGetValue(modDir + "|" + listed[index].File, out var had) ? had.ToArray() : null);

        SelectMod(modDir);
        int at = models.FindIndex(m => string.Equals(m.File, listed[index].File, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return Refuse(404, "model-not-in-mod", "the model was not listed when the mod was opened");
        // Always a fresh solve, so the undo stack holds the harness's actions alone.
        SelectModel(at);
        HarnessSync();

        bool windowOpened = false;
        if (show)
        {
            windowOpened = harnessWindowOpen?.Invoke() == false;
            harnessShowWindow?.Invoke();
        }
        return new StudioOpenOutcome(null, models[at].File, snapshot, windowOpened, volume?.EditVersion ?? -1, View());
    }

    StudioSetOutcome IStudioHarness.Set(StudioSetRequest request)
    {
        HarnessSync();
        static StudioSetOutcome Refuse(int status, string code, string message) => new(new StudioRefusal(status, code, message), [], null, null);
        if (HarnessDragOpen) return Refuse(409, "dragging", "a drag, grab or stroke is under way");
        if (parts is not { } model) return Refuse(409, "studio-changed", "no model is open");
        foreach (var label in request.Lock.Concat(request.Unlock).Select(l => (string?)l).Append(request.Part).OfType<string>())
            if (model.Parts.All(p => p.Label != label)) return Refuse(400, "invalid-field", $"no part {label} in the open model");

        if (request.Tool is { } t) SwitchTool(t switch { "rotate" => Tool.Rotate, "scale" => Tool.Scale, _ => Tool.Move });
        if (request.Mode is { } m) SwitchMoveSelect(m switch { "polygons" => MoveSelectMode.Polygons, "all" => MoveSelectMode.All, _ => MoveSelectMode.Parts });
        if (request.View is { } v) SwitchView(v == "model");
        if (request.Mirror is { } mirror && mirror != mirrorBrush) { mirrorBrush = mirror; viewport.Recolour(); }
        if (request.Adjacent is { } adjacent) moveAdjacent = adjacent;
        if (request.MoveSkin is { } skin && skin != moveSkin) SetMoveSkin(skin);
        HarnessSync();
        if (request.UnlockAll) UnlockAll();

        var locks = new List<(string, string)>();
        foreach (var (label, wantLocked) in request.Lock.Select(l => (l, true)).Concat(request.Unlock.Select(l => (l, false))))
        {
            var part = model.Parts.First(p => p.Label == label);
            if (IsLocked(part) == wantLocked) { locks.Add((label, "unchanged")); continue; }
            if (!SkinMovePolicy.LockAllowed(IsSkin(part), tool == Tool.Retarget, SkinMovesNow)) { locks.Add((label, "refused")); continue; }
            ToggleLock(label);
            locks.Add((label, IsLocked(part) == wantLocked ? (wantLocked ? "locked" : "unlocked") : "refused"));
        }

        bool? chosen = null;
        if (request.Part is { } chosenLabel)
        {
            SelectMovePart(chosenLabel);
            chosen = !moveAll && (movePolygons ? movePolys.Count > 0 : movePart == chosenLabel);
        }
        return new StudioSetOutcome(null, locks, chosen, View());
    }

    StudioDragOutcome IStudioHarness.Drag(StudioDragRequest request, IReadOnlyCollection<string> harnessFiles)
    {
        HarnessSync();
        string? root = modDir is { } dir ? HarnessRoot(dir) : null;
        StudioDragOutcome Refuse(int status, string code, string message)
            => new(new StudioRefusal(status, code, message), request.Kind, false, 0, 0f, null, false, status == 409 ? this.status : null,
                   statusIsError, [], volume?.EditVersion ?? -1, null);
        if (volume is not { } vol || root == null || parts == null) return Refuse(409, "studio-changed", "no model is open");
        if (HarnessDragOpen) return Refuse(409, "dragging", "a drag, grab or stroke is under way");
        var before = RecordFiles(root);

        if (request.Kind == "undo")
        {
            if (!vol.CanUndo) return Refuse(409, "nothing-to-undo", "the Studio has nothing to undo");
            var was = vol.Positions().ToArray();
            // The Undo move button's own body.
            vol.Undo(); AfterBrushEdit(); SaveBrush();
            return Done("undo", was, null, root, before, harnessFiles);
        }

        var wanted = request.Kind switch { "rotate" => Tool.Rotate, "scale" => Tool.Scale, _ => Tool.Move };
        if (tool != wanted)
            return Refuse(409, "wrong-tool", $"the body is a {request.Kind}, but the Studio's tool is {tool}; set the tool first");
        if (MovePivot() is not { } pivot)
            return Refuse(409, "nothing-to-move", moveAll ? Strings.Parts.MoveAllNone : Strings.Parts.MoveNoPart);
        var start = vol.Positions().ToArray();
        // The gizmo's own begin, place and finish, about the gizmo's pivot; then saved at once, in either view.
        if (!BeginPartDrag(pivot)) return Refuse(409, "nothing-to-move", status ?? Strings.Parts.MoveNothingFree);
        if (request.Kind == "move") PlacePartDrag(request.Offset, null);
        else PlacePartDrag(null, TestHarness.HarnessStudio.DragTransform(request, pivot));
        FinishMove();
        FlushPending();
        return Done(request.Kind, start, pivot, root, before, harnessFiles);
    }

    /// <summary>A finished drag's answer: what moved, whether it is on disk, and the session's files hashed now.</summary>
    private StudioDragOutcome Done(string kind, float[] was, Vector3? pivot, string root, HashSet<string> before,
                                   IReadOnlyCollection<string> harnessFiles)
    {
        var now = volume!.Positions();
        int moved = 0;
        for (int v = 0; v * 3 + 2 < was.Length && v * 3 + 2 < now.Length; v++)
            if (was[v * 3] != now[v * 3] || was[v * 3 + 1] != now[v * 3 + 1] || was[v * 3 + 2] != now[v * 3 + 2]) moved++;
        bool saved = brushChangedAt < 0 && !statusIsError;
        var files = RecordFiles(root)
            .Where(f => !before.Contains(f) || harnessFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Select(f => new StudioFile(f, Sha256Of(root, f) ?? ""))
            .ToList();
        return new StudioDragOutcome(null, kind, moved > 0, moved, volume.Worst * 1000f, pivot, saved, status, statusIsError,
                                     files, volume.EditVersion, View());
    }

    StudioPutBack IStudioHarness.EndSession(StudioSession session, bool refreshGame)
    {
        HarnessSync();
        if (HarnessRoot(session.ModDir) is not { } root)
            return new StudioPutBack(session.ModDir, "failed", "none", "penumbra-unavailable", true);

        // Gathered, planned and acted on in this one framework call: nothing can change in between.
        var patched = RecordFiles(root).ToDictionary(f => f, f => Sha256Of(root, f), StringComparer.OrdinalIgnoreCase);
        bool harnessModel = IsOpen(session.ModDir, session.File);
        var plan = TestHarness.HarnessStudio.PlanPutBack(new TestHarness.HarnessStudio.StudioFacts(
            patched, session.Files, harnessModel, volume?.EditVersion ?? -1, session.EditVersion,
            SaveOwed: harnessModel && brushChangedAt >= 0, DragOpen: harnessModel && HarnessDragOpen,
            ApplyingSizes: applySizesTask != null));

        if (plan.Files == "changed")
            return new StudioPutBack(session.ModDir, "changed", "changed", plan.Code, true);

        if (plan.Panel == "restore") CloseModelWithoutSaving();

        string files = plan.Files;
        string? code = null;
        bool keep = false;
        if (plan.Files == "revert")
        {
            if (patched.Keys.Select(f => ModelBackupOrder.LaterFeature(root, MeshVolumeService.BackupSubdir, f)).FirstOrDefault(o => o != null) is { } later)
            {
                files = "failed";
                code = "blocked:" + later;
                keep = true;
            }
            else
            {
                var result = MeshVolumeService.Revert(root);
                var still = RecordFiles(root).Where(session.Files.ContainsKey).ToList();
                if (still.Count > 0 || !result.Ok)
                {
                    files = "failed";
                    code = "still-patched";
                    keep = true;
                }
                else files = "reverted";
                if (refreshGame && result.Ok) ReloadAfterRevert(session.ModDir, root);
            }
        }

        string panel = "changed";
        if (plan.Panel == "restore")
        {
            RestoreSnapshot((HarnessSnapshot)session.Snapshot);
            panel = "restored";
        }
        if (session.WindowOpened && plan.Panel == "restore") harnessCloseWindow?.Invoke();
        return new StudioPutBack(session.ModDir, files, panel, code, keep);
    }

    /// <summary>
    /// After a revert, reload the session's own mod by name — whatever the panel shows — and redraw. The panel's own
    /// refresh only when it shows that mod; a panel on another mod is the user's and is left alone.
    /// </summary>
    private void ReloadAfterRevert(string sessionMod, string root)
    {
        if (string.Equals(modDir, sessionMod, StringComparison.OrdinalIgnoreCase) && vanilla == null)
        {
            AfterModChange(root);
            return;
        }
        penumbra.ReloadModDirectory(sessionMod);
        compositor.TriggerRecomposite("parts-written");
        compositor.RedrawForChangedModel("Studio parts");
    }

    /// <summary>Drop the open model without saving it: the save owed is cleared before anything that could flush it.</summary>
    private void CloseModelWithoutSaving()
    {
        brushChangedAt = -1;
        if (modDir is { } dir) SelectMod(dir);
    }

    /// <summary>The panel as the snapshot had it: settings, the harness model's locks, and the model open before.</summary>
    private void RestoreSnapshot(HarnessSnapshot snap)
    {
        if (snap.Locks is { } locks) lockedParts[snap.LockKey] = new HashSet<string>(locks, StringComparer.Ordinal);
        else lockedParts.Remove(snap.LockKey);

        if (snap.ModDir is { } dir && penumbra.GetModDirectory() is { } mods && Directory.Exists(Path.Combine(mods, dir)))
        {
            SelectMod(dir);
            int at = snap.ModelFile == null ? -1
                   : models.FindIndex(m => string.Equals(m.File, snap.ModelFile, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) SelectModel(at);
        }
        else if (snap.ModDir == null)
        {
            if (modDir is { } open) SelectMod(open);
            modDir = null;
            models = []; modelLabels = []; redirects = [];
            brushSaved = 0;
        }

        SwitchTool(snap.Tool);
        SwitchMoveSelect(snap.Mode);
        SwitchView(snap.ModelView);
        if (mirrorBrush != snap.Mirror) { mirrorBrush = snap.Mirror; viewport.Recolour(); }
        moveAdjacent = snap.Adjacent;
        if (moveSkin != snap.MoveSkin) SetMoveSkin(snap.MoveSkin);
        HarnessSync();
    }

    /// <summary>The files the mod's Studio record lists now.</summary>
    private static HashSet<string> RecordFiles(string root)
        => new(MeshVolumeService.ReadRecord(root)?.Files ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>A mod file's SHA-256 as on disk, hex; null when it cannot be read.</summary>
    private static string? Sha256Of(string root, string rel)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))); }
        catch (Exception) { return null; }
    }
}

/// <summary>The Studio's model list as the harness's matcher takes it.</summary>
internal static class StudioModelMatch
{
    internal static (int Index, string? Error) Match(List<PenumbraModMeta.Redirect> models, string wanted)
        => TestHarness.HarnessStudio.MatchModel(models.Select(m => (m.File, m.GamePath)).ToList(), wanted);
}
#endif
