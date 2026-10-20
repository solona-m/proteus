using System.IO;
using System.Runtime.CompilerServices;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Mode = Proteus.Services.MeshVolumeSolve.MirrorMode;
using Report = Proteus.Services.MeshVolumeSolve.MirrorReport;

namespace Proteus.Tests;

/// <summary>
/// The Studio panel's side of the mirror for Move, Rotate and Scale: when the rainbow shows, what the line under the
/// checkbox says, and — read from the source, since the panel needs ImGui — that one checkbox and one pump serve the
/// brushes, the model view and the character alike.
/// </summary>
public class PartsPanelMirrorTests
{
    private static string Source([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Gui", "PartsPanel.cs"));

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, System.StringComparison.Ordinal);
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

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-7")]
    [Trait("Plan", "EDGE-mirror-move-tools-18")]
    [Trait("Plan", "EDGE-mirror-move-tools-6")]
    public void ShowsMoveFalloffTruthTable()
    {
        // The falloff on: shown, mirror or not.
        Assert.True(PartsPanel.ShowsMoveFalloff(true, true, 20f, false, null));
        Assert.True(PartsPanel.ShowsMoveFalloff(true, true, 20f, true, Mode.Off));
        // The falloff off: shown only for a mirror that matched something.
        Assert.False(PartsPanel.ShowsMoveFalloff(true, false, 20f, false, null));
        Assert.False(PartsPanel.ShowsMoveFalloff(true, false, 20f, true, Mode.Off));
        Assert.False(PartsPanel.ShowsMoveFalloff(true, false, 20f, true, null));
        Assert.True(PartsPanel.ShowsMoveFalloff(true, false, 20f, true, Mode.Sides));
        Assert.True(PartsPanel.ShowsMoveFalloff(true, false, 0f, true, Mode.Centred));
        // Not a move tool: never.
        Assert.False(PartsPanel.ShowsMoveFalloff(false, true, 20f, true, Mode.Sides));
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-5")]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    [Trait("Plan", "EDGE-mirror-move-tools-28")]
    public void MirrorStatusPicksTheRightReport()
    {
        var ps = Strings.Parts;
        var sides = new Report(40, 30, 0, Mode.Sides);
        var held = new Report(40, 30, 5, Mode.Sides);
        var centred = new Report(40, 40, 0, Mode.Centred);
        var none = new Report(40, 0, 0, Mode.Off);

        Assert.Equal(string.Format(ps.MoveMirrorPairedFmt, 30, 40), PartsPanel.MirrorStatus(true, false, null, sides));
        Assert.Equal(string.Format(ps.MoveMirrorPairedFmt, 30, 40) + "\n" + string.Format(ps.MoveMirrorHeldFmt, 5),
                     PartsPanel.MirrorStatus(true, false, null, held));
        Assert.Equal(ps.MoveMirrorCentred, PartsPanel.MirrorStatus(true, false, null, centred));
        Assert.Equal(ps.MoveMirrorNone, PartsPanel.MirrorStatus(true, false, null, none));

        // While a drag is under way its own report, whatever the preview says; after it, the preview's.
        Assert.Equal(ps.MoveMirrorCentred, PartsPanel.MirrorStatus(true, true, centred, sides));
        Assert.Equal(string.Format(ps.MoveMirrorPairedFmt, 30, 40), PartsPanel.MirrorStatus(true, false, centred, sides));

        // Mirror off, or nothing chosen: nothing.
        Assert.Null(PartsPanel.MirrorStatus(false, false, sides, sides));
        Assert.Null(PartsPanel.MirrorStatus(true, false, sides, null));
        Assert.Null(PartsPanel.MirrorStatus(true, false, null, new Report(0, 0, 0, Mode.Off)));
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-2")]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    [Trait("Plan", "EDGE-mirror-move-tools-17")]
    [Trait("Plan", "EDGE-mirror-move-tools-19")]
    [Trait("Plan", "EDGE-mirror-move-tools-28")]
    public void MirrorToggleIsWiredIntoTheMoveTools()
    {
        var src = Source();

        // One checkbox, drawn under the brushes and under the move tools, where a drag under way disables it.
        Assert.Contains("DrawMirrorToggle();", Method(src, "private void DrawBrush()"));
        var move = Method(src, "private void DrawMove()");
        Assert.Contains("using (ImRaii.Disabled(volume is { Moving: true })) DrawMirrorToggle();", move);
        Assert.Contains("MirrorStatus(mirrorBrush,", move);
        Assert.Contains("ps.BrushMirror, ref mirrorBrush", Method(src, "private void DrawMirrorToggle()"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"ref mirrorBrush"));

        // The one pump, for the model view and the character alike, mirrors about each gizmo's own drag pivot.
        var pump = Method(src, "private void PumpMove()");
        Assert.Contains("moveGizmo.DragPivot", pump);
        Assert.Contains("rotateGizmo.DragPivot", pump);
        Assert.Contains("scaleDrag.DragPivot", pump);
        Assert.Contains("mirror: mirror)", pump);

        // The preview is measured again when the mirror or the falloff switch changes.
        var preview = Method(src, "private float[]? MoveFalloffNow()");
        Assert.Contains("mirrorBrush, moveAdjacent);", preview);
        Assert.Contains("mirror: mirrorBrush)", preview);
    }
}
