using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Studio panel's side of the test harness, read from the source (it needs Dalamud): the routes run the code each
/// control runs, never a second path, make no ImGui call between frames, and put back only what is the harness's.
/// </summary>
public class PartsPanelHarnessTests
{
    private static string Gui([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Gui");

    private static string Main() => File.ReadAllText(Path.Combine(Gui(), "PartsPanel.cs"))
                                     + "\n" + File.ReadAllText(Path.Combine(Gui(), "PartsPanel.MoveAll.cs"));

    private static string Harness() => File.ReadAllText(Path.Combine(Gui(), "PartsPanel.Harness.cs"));

    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    private static int Index(string text, string what)
    {
        int at = text.IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {what}");
        return at;
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-13")]
    [Trait("Plan", "EDGE-move-all-parts-25")]
    public void SetUsesTheControlsCode()
    {
        var set = Method(Harness(), "StudioSetOutcome IStudioHarness.Set(");
        foreach (var call in new[] { "SwitchTool(", "SwitchMoveSelect(", "SwitchView(", "SetMoveSkin(", "UnlockAll()", "ToggleLock(", "SelectMovePart(",
                                     "SkinMovePolicy.LockAllowed(" })
            Assert.Contains(call, set);
        var src = Main();
        Assert.Contains("if (clicked) SwitchTool(value);", Method(src, "private void DrawToolPicker()"));
        foreach (var mode in new[] { "Parts", "Polygons", "All" })
            Assert.Contains($"SwitchMoveSelect(MoveSelectMode.{mode});", Method(src, "private void DrawMove()"));
        Assert.Contains("SwitchView(modelView)", src);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-14")]
    [Trait("Plan", "EDGE-move-all-parts-22")]
    public void DragUsesTheGizmoPath()
    {
        var src = Main();
        var pump = Method(src, "private void PumpMove()");
        Assert.Contains("BeginPartDrag(", pump);
        Assert.Contains("PlacePartDrag(", pump);
        var begin = Method(src, "private bool BeginPartDrag(Vector3 dragPivot)");
        Assert.Contains("BeginMove(seeds, AdjacentNow,", begin);
        Assert.Contains("mirror: mirror)", begin);
        Assert.Contains("MoveAllNothingFree", begin);

        var drag = Method(Harness(), "StudioDragOutcome IStudioHarness.Drag(");
        int pivot = Index(drag, "MovePivot()"), b = Index(drag, "BeginPartDrag(pivot)"), p = Index(drag, "PlacePartDrag(");
        int finish = Index(drag, "FinishMove();"), flush = Index(drag, "FlushPending();");
        Assert.True(pivot < b && b < p && p < finish && finish < flush);
        Assert.Contains("HarnessStudio.DragTransform(", drag);
        Assert.Contains("vol.Undo(); AfterBrushEdit(); SaveBrush();", drag);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    [Trait("Plan", "EDGE-move-all-parts-36")]
    [Trait("Plan", "EDGE-move-all-parts-39")]
    public void EndSessionNeverSaves()
    {
        var h = Harness();
        var end = Method(h, "StudioPutBack IStudioHarness.EndSession(");
        // Gathered, planned and acted on in this one call.
        Assert.True(Index(end, "RecordFiles(root)") < Index(end, "HarnessStudio.PlanPutBack("));
        Assert.True(Index(end, "HarnessStudio.PlanPutBack(") < Index(end, "MeshVolumeService.Revert("));
        Assert.Contains("if (plan.Files == \"changed\")", end);
        Assert.Contains("if (plan.Panel == \"restore\") CloseModelWithoutSaving();", end);
        // Re-read after the revert: a success alone is never trusted.
        Assert.True(Index(end, "MeshVolumeService.Revert(") < Index(end, "var still = RecordFiles(root)"));
        Assert.Contains("\"still-patched\"", end);
        Assert.Contains("\"blocked:\" + later", end);
        Assert.Contains("ReloadAfterRevert(session.ModDir, root)", end);

        var reload = Method(h, "private void ReloadAfterRevert(");
        Assert.True(Index(reload, "string.Equals(modDir, sessionMod") < Index(reload, "AfterModChange(root)"));
        Assert.Contains("penumbra.ReloadModDirectory(sessionMod)", reload);
        Assert.Contains("compositor.RedrawForChangedModel(", reload);

        var close = Method(h, "private void CloseModelWithoutSaving()");
        Assert.StartsWith("{\n        brushChangedAt = -1;", close.Replace("\r\n", "\n"));
        Assert.DoesNotContain("SaveBrush", close);
        Assert.DoesNotContain("FlushPending", close);

        var restore = Method(h, "private void RestoreSnapshot(");
        foreach (var call in new[] { "SwitchTool(snap.Tool)", "SwitchMoveSelect(snap.Mode)", "SwitchView(snap.ModelView)", "snap.Mirror", "snap.Adjacent",
                                     "SetMoveSkin(snap.MoveSkin)", "lockedParts[snap.LockKey]" })
            Assert.Contains(call, restore);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    public void NoImGuiBetweenFrames()
    {
        var src = Main();
        foreach (var sig in new[] { "private bool BeginPartDrag(Vector3 dragPivot)", "private void PlacePartDrag(", "private void SwitchTool(Tool value)",
                                    "private void SwitchMoveSelect(MoveSelectMode value)", "private void SwitchView(bool modelView)" })
            Assert.DoesNotContain("ImGui.", Method(src, sig));
        Assert.DoesNotContain("ImGui.", Harness());
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    [Trait("Plan", "EDGE-move-all-parts-38")]
    public void SwitchToolKeepsGuard_OpenAlwaysSelects()
    {
        Assert.StartsWith("{\n        if (tool == value) return;", Method(Main(), "private void SwitchTool(Tool value)").Replace("\r\n", "\n"));
        var open = Method(Harness(), "StudioOpenOutcome IStudioHarness.Open(");
        Assert.True(Index(open, "SelectMod(modDir)") < Index(open, "SelectModel(at)"));
        Assert.True(Index(open, "SelectModel(at)") < Index(open, "return new StudioOpenOutcome(null"));
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-18")]
    [Trait("Plan", "EDGE-move-all-parts-19")]
    public void OpenRefusesSavedEdits_AndNeverFlushes()
    {
        var open = Method(Harness(), "StudioOpenOutcome IStudioHarness.Open(");
        Assert.True(Index(open, "\"studio-busy\"") < Index(open, "SelectMod(modDir)"));
        Assert.True(Index(open, "MeshVolumeService.PatchedCount(root) > 0") < Index(open, "SelectMod(modDir)"));
        Assert.DoesNotContain("FlushPending", open);
        Assert.DoesNotContain("FinishMove", open);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-31")]
    [Trait("Plan", "EDGE-move-all-parts-33")]
    public void EveryMemberSyncs_AndShowOpensTheStudioTab()
    {
        var h = Harness();
        Assert.Contains("private void HarnessSync() => UpdateSkinMoves();", h);
        foreach (var sig in new[] { "public bool IsOpen(", "public IReadOnlyDictionary<string, object?> View()", "StudioOpenOutcome IStudioHarness.Open(",
                                    "StudioSetOutcome IStudioHarness.Set(", "StudioDragOutcome IStudioHarness.Drag(", "StudioPutBack IStudioHarness.EndSession(" })
            Assert.StartsWith("{\n        HarnessSync();", Method(h, sig).Replace("\r\n", "\n"));
        Assert.Contains("harnessShowWindow?.Invoke();", Method(h, "StudioOpenOutcome IStudioHarness.Open("));
        Assert.Contains("if (session.WindowOpened && plan.Panel == \"restore\") harnessCloseWindow?.Invoke();",
                        Method(h, "StudioPutBack IStudioHarness.EndSession("));
        var window = File.ReadAllText(Path.Combine(Gui(), "StatusWindow.cs"));
        Assert.Contains("_forceStudioTab = true;", Method(window, "internal void OpenToStudio()"));
        Assert.Contains("Show(forceExpand: true);", Method(window, "internal void OpenToStudio()"));
        Assert.Contains("_forceStudioTab ? ImGuiTabItemFlags.SetSelected", window);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-16")]
    public void HarnessPartialIsCompiledOut()
    {
        var h = Harness().Replace("\r\n", "\n").Trim();
        Assert.StartsWith("#if PROTEUS_TEST_HARNESS", h);
        Assert.EndsWith("#endif", h);
        foreach (var file in Directory.GetFiles(Gui(), "*.cs").Where(f => !f.EndsWith("PartsPanel.Harness.cs", StringComparison.Ordinal)))
            Assert.DoesNotContain("IStudioHarness", File.ReadAllText(file));
    }
}
