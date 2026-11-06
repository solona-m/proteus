using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Proteus.Gui;
using Proteus.Localization;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Studio panel's side of the "All" mode of Move, Rotate and Scale — read from the source, since the panel needs
/// ImGui: the third radio, the branches that make every ticked part one selection, and the settings switched off.
/// </summary>
public class PartsPanelMoveAllTests
{
    private static string Gui([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Gui");

    /// <summary>PartsPanel.cs and its All partial, read as one.</summary>
    private static string Source()
    {
        string main = File.ReadAllText(Path.Combine(Gui(), "PartsPanel.cs"));
        string all = Path.Combine(Gui(), "PartsPanel.MoveAll.cs");
        return File.Exists(all) ? main + "\n" + File.ReadAllText(all) : main;
    }

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
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

    /// <summary>The expression body of a one-line member (=> ... ;).</summary>
    private static string Expression(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int end = source.IndexOf(";\n", at, StringComparison.Ordinal);
        return source[at..end];
    }

    private static int Index(string text, string what)
    {
        int at = text.IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {what}");
        return at;
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-1")]
    [Trait("Plan", "EDGE-move-all-parts-17")]
    public void DrawMoveOffersAllAfterPolygons()
    {
        var move = Method(Source(), "private void DrawMove()");
        int parts = Index(move, "ImGui.RadioButton(ps.MoveSelectParts,");
        int polys = Index(move, "ImGui.RadioButton(ps.MoveSelectPolygons,");
        int all = Index(move, "ImGui.RadioButton(ps.MoveSelectAll,");
        Assert.True(parts < polys && polys < all);
        Assert.Equal(3, Regex.Matches(move, @"&& volume is not \{ Moving: true \}\)").Count);
        Assert.Contains("ImGui.SetTooltip(ps.MoveSelectAllTip)", move);
        Assert.Contains("MoveAllSelection.RadiosFit(", move);
        Assert.Equal(2, Regex.Matches(move, @"if \(oneLine\) ImGui\.SameLine\(\);").Count);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-2")]
    public void AllFeedsTheOnePumpForBothViews()
    {
        var src = Source();
        Assert.Contains("moveAll ? (AllNow().Seeds is { Length: > 0 } all ? all : null)",
                        Expression(src, "private IEnumerable<int>? MoveSeeds()"));
        Assert.Contains("if (moveAll) return AllPivot();", Method(src, "private Vector3? MovePivot()"));
        // The gizmo's pump starts every drag through the one shared begin.
        Assert.Contains("BeginPartDrag(", Method(src, "private void PumpMove()"));
        var begin = Method(src, "private bool BeginPartDrag(Vector3 dragPivot)");
        Assert.Contains("volume.BeginMove(seeds, AdjacentNow,", begin);
        Assert.Contains("mirror = MirrorNow", begin);
        // One pump, run once a frame after the live brush is armed, for the model view and the character alike.
        Assert.Single(Regex.Matches(src, @"\bPumpMove\(\);"));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-3")]
    public void EvenScaleTakesAnyTickedPart()
    {
        var src = Source();
        foreach (var sig in new[] { "private bool MoveTicked(int index)", "private bool IsChosenPart(string label)" })
        {
            var body = Method(src, sig);
            Assert.True(Index(body, "if (moveAll)") < Index(body, "movePart == null"), sig);
            Assert.True(Index(body, "MoveAllSelection.Moves(") < Index(body, "movePart == null"), sig);
        }
        Assert.Contains("viewport.Hovered is { } under && IsChosenPart(under)", Method(src, "private void DrawParts("));
        Assert.Contains("partTicked: moving ? moveTickedFn", src);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-5")]
    public void PivotAndEmptyWarning()
    {
        var src = Source();
        var pivot = Method(src, "private Vector3? AllPivot()");
        Assert.Contains("AllNow()", pivot);
        Assert.Contains("sel.Seeds.Length == 0 ? null", pivot);
        Assert.Contains("volume.EditVersion", pivot);
        var move = Method(src, "private void DrawMove()");
        Assert.Contains("if (all.Seeds.Length == 0) ImGui.TextColored(ProteusStyle.Warn, ps.MoveAllNone);", move);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-6")]
    public void ListUnderAllIsTickBoxesWithLabels()
    {
        var src = Source();
        var rows = Method(src, "private void DrawPartRows(");
        int allBranch = Index(rows, "if (moving && moveAll)");
        int partsBranch = Index(rows, "else if (moving)");
        var branch = rows[allBranch..partsBranch];
        Assert.Contains("ImGui.Checkbox($\"{part.Label}##mvl_{part.Label}\"", branch);
        Assert.DoesNotContain("##mv_", branch);
        Assert.Contains("ps.MoveAllListTip", branch);
        Assert.Contains("ps.MoveSkinTip", branch);
        Assert.Contains("moving && moveAll", Method(src, "private float WidestRow("));
        Assert.Contains("moveAll ? Locks.Contains(part.Label) : movePart == part.Label", Method(src, "private List<PartRow> ListedRows("));
        Assert.Contains("moveAll ? ps.MoveAllListTip : ps.MoveListTip", Method(src, "private string PartListTip()"));
        Assert.Contains("string.Format(ps.PartsTickedFmt, AllNow().MovingRows, AllNow().Rows)",
                        Method(src, "private bool DrawPartHeader("));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-6")]
    public void CountLineSaysIncludingSkin()
        => Assert.Contains("all.IncludesSkin ? ps.MoveAllSkinFmt : ps.MoveAllFmt", Method(Source(), "private void DrawMove()"));

    [Fact]
    [Trait("Plan", "AC-move-all-parts-6")]
    public void EveryHelpTextMaps()
    {
        var ps = Strings.Parts;
        var values = Enum.GetValues<MoveHelpText>();
        Assert.Equal(12, values.Length);
        Assert.All(values, v => Assert.False(string.IsNullOrWhiteSpace(PartsPanel.HelpText(v))));
        Assert.Equal(ps.MoveHelp, PartsPanel.HelpText(MoveHelpText.MoveHelp));
        Assert.Equal(ps.MoveLiveHint, PartsPanel.HelpText(MoveHelpText.MoveLiveHint));
        Assert.Equal(ps.RotateHelp, PartsPanel.HelpText(MoveHelpText.RotateHelp));
        Assert.Equal(ps.RotateLiveHint, PartsPanel.HelpText(MoveHelpText.RotateLiveHint));
        Assert.Equal(ps.ScaleHelp, PartsPanel.HelpText(MoveHelpText.ScaleHelp));
        Assert.Equal(ps.ScaleLiveHint, PartsPanel.HelpText(MoveHelpText.ScaleLiveHint));
        var allTexts = new[]
        {
            MoveHelpText.MoveAllHelp, MoveHelpText.MoveAllLiveHint, MoveHelpText.RotateAllHelp,
            MoveHelpText.RotateAllLiveHint, MoveHelpText.ScaleAllHelp, MoveHelpText.ScaleAllLiveHint,
        }.Select(PartsPanel.HelpText).ToList();
        Assert.Equal(6, allTexts.Distinct().Count());
        Assert.Equal(new[] { ps.MoveAllHelp, ps.MoveAllLiveHint, ps.RotateAllHelp, ps.RotateAllLiveHint, ps.ScaleAllHelp,
                             ps.ScaleAllLiveHint }, allTexts);
        Assert.Contains("HelpText(MoveAllSelection.HelpFor(", Method(Source(), "private void DrawMove()"));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-6")]
    [Trait("Plan", "EDGE-move-all-parts-9")]
    public void SelectMovePartRefusesUnderAll()
    {
        var body = Method(Source(), "private void SelectMovePart(string label)");
        Assert.StartsWith("{\n        if (!MoveAllSelection.ClickChooses(moveSelect)) return;", body.Replace("\r\n", "\n"));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-7")]
    [Trait("Plan", "EDGE-move-all-parts-10")]
    public void MoveToolsUseMirrorNowOnly()
    {
        var src = Source();
        var move = Method(src, "private void DrawMove()");
        Assert.Contains("using (ImRaii.Disabled(moveAll)) ImGui.Checkbox(ps.MoveAdjacent, ref moveAdjacent);", move);
        Assert.Contains("using (ImRaii.Disabled(!moveAdjacent || moveAll))", move);
        Assert.Contains("using (ImRaii.Disabled(volume is { Moving: true } || moveAll)) DrawMirrorToggle(moveAll ? ps.MoveAllOffTip : null);", move);
        Assert.Equal(3, Regex.Matches(move, @"ps\.MoveAllOffTip").Count);

        foreach (var body in new[] { Method(src, "private void PumpMove()"), Method(src, "private bool BeginPartDrag(Vector3 dragPivot)"),
                                     Method(src, "private float[]? MoveFalloffNow()"), move })
        {
            Assert.DoesNotContain("mirrorBrush", body);
            Assert.DoesNotContain("moveAdjacent,", body.Replace("ref moveAdjacent", ""));
        }
        Assert.Contains("viewport.MirrorBrush = MirrorNow;", src);
        Assert.Contains("mirror: MirrorNow, partOf:", src);
        Assert.Contains("MoveAllSelection.MirrorApplies(moveSelect, mirrorBrush)", src);

        // The brushes keep the mirror as ticked.
        Assert.Contains("surface.ToViewer, mirrorBrush);", src);
        Assert.Contains("relaxRatePercent / 100f, mirrorBrush,", src);
        Assert.Contains("smoothRatePercent / 100f, mirrorBrush,", src);
        Assert.Contains("vol.BeginGrab(at, ActiveRadiusMm / 1000f, mirrorBrush);", src);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-10")]
    public void SettingsNotOverwrittenUnderAll()
    {
        var src = Source();
        Assert.DoesNotMatch(@"\bmoveAdjacent\s*=[^=]", src.Replace("private bool moveAdjacent = true;", ""));
        Assert.DoesNotMatch(@"\bmirrorBrush\s*=[^=]", src);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-8")]
    [Trait("Plan", "EDGE-move-all-parts-15")]
    public void AllEndsThroughFinishMove()
    {
        var src = Source();
        Assert.Contains("if (ended) FinishMove();", Method(src, "private void PumpMove()"));
        Assert.Contains("SaveBrush(refreshGame)", Method(src, "private void FinishMove("));
        string vanilla = File.ReadAllText(Path.Combine(Gui(), "PartsPanel.VanillaCopy.cs"));
        Assert.DoesNotContain("moveAll", vanilla);
        Assert.DoesNotContain("MoveSelectMode", vanilla);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-9")]
    public void ModeDefaultsToWholePartsAndIsNotSaved()
    {
        var src = Source();
        Assert.Contains("private MoveSelectMode moveSelect = MoveSelectMode.Parts;", src);
        var names = typeof(Configuration).GetMembers().Select(m => m.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("MoveSelect", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("MoveAll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-9")]
    [Trait("Plan", "EDGE-move-all-parts-16")]
    public void PolygonsToAllRebuildsTheTint()
    {
        var src = Source();
        var move = Method(src, "private void DrawMove()");
        var allRadio = move[Index(move, "ImGui.RadioButton(ps.MoveSelectAll,")..Index(move, "ImGui.SetTooltip(ps.MoveSelectAllTip)")];
        Assert.Contains("SwitchMoveSelect(MoveSelectMode.All);", allRadio);
        Assert.Contains("if (value == MoveSelectMode.All) allSelection.Touch();",
                        Method(src, "private void SwitchMoveSelect(MoveSelectMode value)"));
        Assert.Contains("moveAll ? AllNow().Version ^ AllTintSalt", src);
        var salt = Regex.Match(src, @"AllTintSalt = (0x[0-9A-F]+);").Groups[1].Value;
        var moveSalt = Regex.Match(src, @"MoveVersion\(\) => .*\^ (0x[0-9A-F]+);").Groups[1].Value;
        Assert.NotEqual("", salt);
        Assert.NotEqual("", moveSalt);
        Assert.NotEqual(salt, moveSalt);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-5")]
    public void NoChangesDuringADrag()
    {
        var rows = Method(Source(), "private void DrawPartRows(");
        var branch = rows[Index(rows, "if (moving && moveAll)")..Index(rows, "else if (moving)")];
        Assert.True(Index(branch, "using (ImRaii.Disabled(volume is { Moving: true }))")
                    < Index(branch, "ImGui.Checkbox($\"{part.Label}##mvl_"));
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-6")]
    public void EnteringAllClearsOtherSelections()
    {
        var src = Source();
        var move = Method(src, "private void DrawMove()");
        // Each radio switches through the one shared body, which drops the chosen part always and the polygons
        // whenever the new mode is not Polygons.
        foreach (var mode in new[] { "Parts", "Polygons", "All" })
            Assert.Contains($"SwitchMoveSelect(MoveSelectMode.{mode});", move);
        var body = Method(src, "private void SwitchMoveSelect(MoveSelectMode value)");
        Assert.Contains("movePart = null;", body);
        Assert.Contains("if (value != MoveSelectMode.Polygons) ClearPolygons();", body);
        Assert.Contains("if (moveSelect == value || volume is { Moving: true }) return;", body);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-7")]
    public void ModeCarriesAcrossPartTools()
        => Assert.DoesNotContain("moveSelect", Method(Source(), "private void DrawToolPicker("));

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-7")]
    public void MoveAllNeedsAPartTool()
    {
        var src = Source();
        Assert.Contains("private bool moveAll => PartTool && moveSelect == MoveSelectMode.All;", src);
        // Every caller of AllNow() reaches it only under moveAll.
        foreach (Match m in Regex.Matches(src, @"AllNow\(\)"))
        {
            int lineStart = src.LastIndexOf('\n', m.Index) + 1;
            string line = src[lineStart..src.IndexOf('\n', m.Index)];
            if (line.Contains("private MoveAllSelection AllNow()")) continue;
            bool guarded = line.Contains("moveAll") || line.Contains("var all = AllNow();") || line.Contains("var sel = AllNow();");
            Assert.True(guarded, $"unguarded AllNow(): {line.Trim()}");
        }
        // The two block-bodied callers sit inside a moveAll branch, or are reached only through one.
        var move = Method(src, "private void DrawMove()");
        Assert.True(Index(move, "if (moveAll)") < Index(move, "var all = AllNow();"));
        Assert.Contains("if (moveAll) return AllPivot();", Method(src, "private Vector3? MovePivot()"));
        Assert.Equal(2, Regex.Matches(src, @"\bAllPivot\(\)").Count);   // its declaration, and that one call
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-12")]
    public void HeldAllSaysNothingFree()
    {
        var begin = Method(Source(), "private bool BeginPartDrag(Vector3 dragPivot)");
        Assert.Contains("status = moveAll ? Strings.Parts.MoveAllNothingFree : Strings.Parts.MoveNothingFree;", begin);
        Assert.Contains("statusIsError = true;", begin);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-14")]
    public void PivotIsCached()
    {
        var pivot = Method(Source(), "private Vector3? AllPivot()");
        Assert.Contains("var key = (sel.Version, volume.EditVersion, (object?)volume);", pivot);
        Assert.Contains("if (allPivotKey != key)", pivot);
    }
}
