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

    /// <summary>A Studio edit is under way or not saved yet: a matcap job refuses (studio-busy) rather than flush it.</summary>
    public bool EditPending() => HarnessDragOpen || brushChangedAt >= 0;

    MatcapHarnessPort IStudioHarness.Matcaps()
    {
        matcapIndex ??= new Services.Matcap.MatcapIndex(null);
        return new MatcapHarnessPort(matcapIndex, MatcapImageFolder(), new Services.Matcap.PenumbraMatcapAdapter(penumbra),
                                     MatcapEnv(), penumbra.GetModDirectory(), penumbra.GetAllModsQuiet(),
                                     gamePath => compositor.ForgetFaceUpstream(gamePath),
                                     (root, gamePath, written) => DrawnElsewhere(root, gamePath, written),
                                     matcapPack);
    }

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
            ["tool"] = tool switch { Tool.Move => "move", Tool.Rotate => "rotate", Tool.Scale => "scale", Tool.Matcap => "matcap", _ => "other" },
            ["mode"] = moveSelect switch { MoveSelectMode.Polygons => "polygons", MoveSelectMode.All => "all", _ => "parts" },
            ["view"] = showModelView ? "model" : "character",
            ["settings"] = new { mirror = mirrorBrush, adjacent = moveAdjacent, moveSkin },
            ["effective"] = new { mirrorNow = MirrorNow, adjacentNow = AdjacentNow, skinMovesNow = SkinMovesNow,
                                  followReady = (parts != null ? volume : null)?.FollowReady == true },
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
            ["edit"] = parts != null && volume is { } v   // the model closed (put-back, no mod open): no edit to show
                ? new { dirty = v.Dirty, canUndo = v.CanUndo, worstMm = v.Worst * 1000f, saveOwed = brushChangedAt >= 0, editVersion = v.EditVersion }
                : null,
            ["status"] = status is { } text ? new StudioStatusLine(text, statusIsError) : null,
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

    StudioSetOutcome IStudioHarness.Set(StudioSetRequest request, StudioSession session)
    {
        HarnessSync();
        StudioSetOutcome Refuse(int status, string code, string message)
            => new(new StudioRefusal(status, code, message), [], null, volume?.EditVersion ?? -1, null);
        if (HarnessDragOpen) return Refuse(409, "dragging", "a drag, grab or stroke is under way");
        if (parts is not { } model || HarnessRoot(session.ModDir) is not { } root) return Refuse(409, "studio-changed", "no model is open");
        // A tool or view switch flushes a save owed: only ever the harness's, never one the user left.
        if (Touched(session, root) is { } touched)
            return Refuse(409, "studio-changed", $"the model or its files changed since the harness last left them ({touched}); put back");
        foreach (var label in request.Lock.Concat(request.Unlock).Select(l => (string?)l).Append(request.Part).OfType<string>())
            if (model.Parts.All(p => p.Label != label)) return Refuse(400, "invalid-field", $"no part {label} in the open model");
        // Locks are the Move tools' (Body size's holds are another set, which put-back does not keep).
        var toolAfter = request.Tool is { } named ? named switch { "rotate" => Tool.Rotate, "scale" => Tool.Scale, _ => Tool.Move } : tool;
        if ((request.UnlockAll || request.Lock.Count > 0 || request.Unlock.Count > 0) && !IsPartTool(toolAfter))
            return Refuse(409, "wrong-tool", "locks are set under Move, Rotate or Scale; set the tool first");

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
            int polysBefore = movePolysVersion;
            SelectMovePart(chosenLabel);
            // Taken only if this call changed the choice to this part: a refused pick leaves the old selection standing.
            chosen = !moveAll && (movePolygons ? movePolysVersion != polysBefore && movePolys.Count > 0 : movePart == chosenLabel);
        }
        return new StudioSetOutcome(null, locks, chosen, volume?.EditVersion ?? -1, View());
    }

    StudioDragOutcome IStudioHarness.Drag(StudioDragRequest request, StudioSession session)
    {
        HarnessSync();
        string? root = HarnessRoot(session.ModDir);
        StudioDragOutcome Refuse(int status, string code, string message)
            => new(new StudioRefusal(status, code, message), request.Kind, false, 0, 0f, null, false, status == 409 ? this.status : null,
                   statusIsError, [], volume?.EditVersion ?? -1, null);
        if (volume is not { } vol || root == null || parts == null) return Refuse(409, "studio-changed", "no model is open");
        if (HarnessDragOpen) return Refuse(409, "dragging", "a drag, grab or stroke is under way");
        // Only from exactly where the harness left the model: an edit of the user's (or a save they owe) would
        // otherwise be folded into the session, and put-back would take it for the harness's and revert it.
        if (Touched(session, root) is { } touched)
            return Refuse(409, "studio-changed", $"the model or its files changed since the harness last left them ({touched}); put back");
        var before = RecordFiles(root);

        if (request.Kind == "undo")
        {
            if (!vol.CanUndo) return Refuse(409, "nothing-to-undo", "the Studio has nothing to undo");
            var was = vol.Positions().ToArray();
            // The Undo move button's own body.
            vol.Undo(); AfterBrushEdit(); SaveBrush();
            // A save that failed is not left owed: it would land in the mod later, outside the session.
            if (brushChangedAt >= 0) brushChangedAt = -1;
            return Done("undo", was, PartTool ? MovePivot() : null, root, before, session);
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
        bool failed = brushChangedAt >= 0;
        if (failed && vol.CanUndo)
        {
            // The save failed: the drag is taken back at once rather than left owed, which would write it into the mod
            // later (a retry, leaving the tab, unload), outside the session.
            vol.Undo();
            brushChangedAt = -1;
            viewport.PositionOverride = vol.Positions();
            viewport.GeometryChanged();
        }
        else if (failed) brushChangedAt = -1;
        return Done(request.Kind, start, pivot, root, before, session, savedOverride: failed ? false : null);
    }

    /// <summary>A finished drag's answer: what moved, whether it is on disk, and the session's files hashed now.</summary>
    private StudioDragOutcome Done(string kind, float[] was, Vector3? pivot, string root, HashSet<string> before,
                                   StudioSession session, bool? savedOverride = null)
    {
        var now = volume!.Positions();
        int moved = 0;
        for (int v = 0; v * 3 + 2 < was.Length && v * 3 + 2 < now.Length; v++)
            if (was[v * 3] != now[v * 3] || was[v * 3 + 1] != now[v * 3 + 1] || was[v * 3 + 2] != now[v * 3 + 2]) moved++;
        bool saved = savedOverride ?? (brushChangedAt < 0 && !statusIsError);
        var files = RecordFiles(root)
            .Where(f => !before.Contains(f) || session.Files.ContainsKey(f))
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
        var facts = HarnessFacts(session, root);
        var patched = facts.Patched;
        var plan = TestHarness.HarnessStudio.PlanPutBack(facts);

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
                if (refreshGame && result.Ok) ReloadAfterRevert(session.ModDir, root, plan.ReloadByName);
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
    /// After a revert, reload the session's own mod by name and redraw. The panel's own refresh (which reselects its
    /// model) only when the harness closed its model and the panel holds nothing of the user's
    /// (<paramref name="byNameOnly"/> false); otherwise the panel is the user's — another model, perhaps of the same
    /// mod — and only its saved-edits count is brought up to date.
    /// </summary>
    private void ReloadAfterRevert(string sessionMod, string root, bool byNameOnly)
    {
        bool showsMod = string.Equals(modDir, sessionMod, StringComparison.OrdinalIgnoreCase) && vanilla == null;
        if (!byNameOnly && showsMod)
        {
            AfterModChange(root);
            return;
        }
        penumbra.ReloadModDirectory(sessionMod);
        compositor.TriggerRecomposite("parts-written");
        compositor.RedrawForChangedModel("Studio parts");
        if (showsMod)
        {
            existing = MeshToggleService.ReadRecord(root);
            brushSaved = MeshVolumeService.PatchedCount(root);
        }
    }

    /// <summary>What put-back (and every drag or set first) judges: the record's files and hashes now, and the model.</summary>
    private TestHarness.HarnessStudio.StudioFacts HarnessFacts(StudioSession session, string root)
    {
        var patched = RecordFiles(root).ToDictionary(f => f, f => Sha256Of(root, f), StringComparer.OrdinalIgnoreCase);
        bool harnessModel = IsOpen(session.ModDir, session.File);
        return new TestHarness.HarnessStudio.StudioFacts(
            patched, session.Files, harnessModel, volume?.EditVersion ?? -1, session.EditVersion,
            SaveOwed: harnessModel && brushChangedAt >= 0, DragOpen: harnessModel && HarnessDragOpen,
            ApplyingSizes: applySizesTask != null);
    }

    /// <summary>Why the session's model or files are not as the harness left them, or null when they are.</summary>
    private string? Touched(StudioSession session, string root)
        => TestHarness.HarnessStudio.PlanPutBack(HarnessFacts(session, root)) is { Files: "changed" } plan ? plan.Code : null;

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
