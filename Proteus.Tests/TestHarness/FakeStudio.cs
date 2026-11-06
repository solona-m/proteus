using System;
using System.Collections.Generic;
using System.Numerics;
using Proteus.TestHarness;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The Studio as the harness sees it, scripted: what each member answers, and every call (with its arguments and the
/// thread it ran on, recorded through the game's <c>Fw</c>).
/// </summary>
internal sealed class FakeStudio(Action<string> fw) : IStudioHarness
{
    public string? OpenMod;
    public string? OpenFile;
    public string Tool = "move";
    public StudioRefusal? OpenRefusal;
    public StudioRefusal? SetRefusal;
    public StudioRefusal? DragRefusal;
    public List<(string Label, string Result)> LockResults = [];
    public bool? Chosen;
    public int EditVersion = 1;
    public string Status = "Saved.";
    public bool StatusIsError;
    public bool Saved = true;
    public List<StudioFile> DragFiles = [new("chara/model/top.mdl", "AA")];
    public Func<StudioSession, StudioPutBack>? EndAnswer;

    public List<(string Mod, string Model, bool Show)> Opens = [];
    public List<StudioSetRequest> Sets = [];
    public List<(StudioDragRequest Request, IReadOnlyCollection<string> HarnessFiles)> Drags = [];
    /// <summary>Set to a reason to answer set and drag as the panel does when the model was touched since the harness left it.</summary>
    public string? Touched;
    public List<StudioSession> Ended = [];

    public IReadOnlyDictionary<string, object?> View()
    {
        fw(nameof(View));
        return new Dictionary<string, object?>
        {
            ["open"] = OpenMod is { } m ? new { modDir = m, file = OpenFile, gamePath = "chara/x.mdl" } : null,
            ["tool"] = Tool, ["mode"] = "all", ["view"] = "character",
            ["settings"] = new { mirror = false, adjacent = true, moveSkin = false },
            ["effective"] = new { mirrorNow = false, adjacentNow = false, skinMovesNow = false, followReady = false },
            ["parts"] = new[] { new { label = "1.1", material = "mt_top.mtrl", island = -1, skin = false, locked = false, moves = true } },
            ["chosen"] = new { part = (string?)null, polygons = 0 },
            ["all"] = new { seeds = 4, movingRows = 1, rows = 1, includesSkin = false, pivot = new[] { 0f, 1f, 0f } },
            ["help"] = "MoveAllLiveHint",
            ["edit"] = new { dirty = false, canUndo = false, worstMm = 0f, saveOwed = false, editVersion = EditVersion },
            ["status"] = new StudioStatusLine(Status, StatusIsError),
        };
    }

    public bool IsOpen(string modDir, string file)
    {
        fw(nameof(IsOpen));
        return string.Equals(OpenMod, modDir, StringComparison.OrdinalIgnoreCase)
            && string.Equals(OpenFile, file, StringComparison.OrdinalIgnoreCase);
    }

    public StudioOpenOutcome Open(string modDir, string model, bool show)
    {
        fw(nameof(Open));
        Opens.Add((modDir, model, show));
        if (OpenRefusal != null) return new StudioOpenOutcome(OpenRefusal, null, null, false, -1, null);
        OpenMod = modDir;
        OpenFile = model;
        return new StudioOpenOutcome(null, model, "snapshot", show, EditVersion, View());
    }

    public StudioSetOutcome Set(StudioSetRequest request, StudioSession session)
    {
        fw(nameof(Set));
        if (Touched != null) return new StudioSetOutcome(new StudioRefusal(409, "studio-changed", Touched), [], null, EditVersion + 100, null);
        Sets.Add(request);
        if (request.Tool is { } t) Tool = t;
        return SetRefusal != null ? new StudioSetOutcome(SetRefusal, [], null, EditVersion, null)
                                  : new StudioSetOutcome(null, LockResults, Chosen, EditVersion, View());
    }

    public StudioDragOutcome Drag(StudioDragRequest request, StudioSession session)
    {
        fw(nameof(Drag));
        if (Touched != null)
            return new StudioDragOutcome(new StudioRefusal(409, "studio-changed", Touched), request.Kind, false, 0, 0f, null, false, Status, true, [], EditVersion + 100, null);
        Drags.Add((request, [.. session.Files.Keys]));
        if (DragRefusal != null)
            return new StudioDragOutcome(DragRefusal, request.Kind, false, 0, 0f, null, false, Status, true, [], EditVersion, null);
        EditVersion++;
        return new StudioDragOutcome(null, request.Kind, true, 12, 30f, new Vector3(0f, 1f, 0f), Saved, Status, StatusIsError,
                                     DragFiles, EditVersion, View());
    }

    public StudioPutBack EndSession(StudioSession session, bool refreshGame)
    {
        fw(nameof(EndSession));
        Ended.Add(session);
        return EndAnswer?.Invoke(session) ?? new StudioPutBack(session.ModDir, "reverted", "restored", null, false);
    }
}
