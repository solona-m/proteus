using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Proteus.Services;
using Xunit;
using A = Proteus.Services.CompositorService.PostSettleAction;
using R = Proteus.Services.CompositorService.PostSettleReading;

namespace Proteus.Tests;

/// <summary>
/// The post-redraw backstop (#57): after a redraw it samples the drawn character until two readings agree, then
/// corrects the composite if the body changed. A mid-reload walk — a body type with no char code — fired a ~206 s
/// composite after three separate redraws on NIRÆ. These call the production decision, the one the loop calls.
/// </summary>
public class PostSettleTests
{
    private const string LastBody = "bibo,gen2", LastChar = "c0201", LastShapes = "shp_a";

    private static CompositorService.PostSettleVerdict Decide(bool final, params R?[] readings)
        => CompositorService.DecidePostSettle(readings, final, LastBody, LastChar, LastShapes);

    /// <summary>Feed the readings one at a time, as the loop does, until it stops sampling.</summary>
    private static CompositorService.PostSettleVerdict Run(params R?[] readings)
    {
        var seen = new List<R?>();
        CompositorService.PostSettleVerdict v = default;
        for (int i = 0; i < readings.Length; i++)
        {
            seen.Add(readings[i]);
            v = CompositorService.DecidePostSettle(seen, i == readings.Length - 1, LastBody, LastChar, LastShapes);
            if (v.Action != A.KeepSampling) break;
        }
        return v;
    }

    private static readonly R Same = new(LastBody, LastChar, LastShapes);
    private static readonly R Incoherent = new("gen2", null, "shp_b");   // the issue's reading, shapes moved too

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-7")]
    [Trait("Plan", "EDGE-issue-sweep-a-18")]
    public void An_incoherent_reading_never_confirms_or_corrects()
    {
        // The issue's sequence: an incoherent reading every time. Never a correction, and the dropped facts are named.
        var v = Run(Incoherent, Incoherent, Incoherent, Incoherent, Incoherent, Incoherent);
        Assert.Equal(A.None, v.Action);
        Assert.Contains("shapes", v.Dropped);
        // Two incoherent readings with equal keys never confirm one another.
        Assert.Equal(A.KeepSampling, Decide(false, Incoherent, Incoherent).Action);
        // A coherent reading then an incoherent last one: nothing to act on at the cap.
        var changed = new R(LastBody, LastChar, "shp_b");
        var last = Decide(true, changed, Incoherent);
        Assert.Equal(A.None, last.Action);
        Assert.Equal(-1, last.Latest);
        Assert.NotNull(last.Dropped);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-7")]
    [Trait("Plan", "EDGE-issue-sweep-a-18")]
    public void Coherent_changes_still_correct()
    {
        // Two agreeing readings with new shapes: a correction that publishes them.
        var shapes = new R(LastBody, LastChar, "shp_b");
        var v = Run(shapes, shapes);
        Assert.Equal(A.Correct, v.Action);
        Assert.Equal(1, v.Latest);
        Assert.True(v.Confirmed && v.ShapesChanged && v.PublishShapes);

        // A body type still moving at the cap: a correction with a fresh walk, nothing published.
        var moving = Run(new R("tbse", LastChar, LastShapes), new R("bibo", LastChar, LastShapes), new R("tbse", LastChar, LastShapes),
                         new R("bibo", LastChar, LastShapes), new R("tbse", LastChar, LastShapes), new R("tbse,gen3", LastChar, LastShapes));
        Assert.Equal(A.CorrectWithRewalk, moving.Action);
        Assert.False(moving.Confirmed || moving.PublishShapes);
        Assert.True(moving.BodyTypeChanged);

        // Nothing changed: no correction.
        Assert.Equal(A.None, Run(Same, Same).Action);

        // Blank walks reset confirmation and never agree with each other.
        Assert.Equal(A.KeepSampling, Decide(false, shapes, null).Action);
        Assert.Equal(A.KeepSampling, Decide(false, null, null).Action);
        Assert.Equal(A.None, Decide(true, shapes, null).Action);
        // A confirmed change of char code, shapes unread: corrects, publishes no shapes.
        var chars = new R(LastBody, "c0101", null);
        var c = Run(chars, chars);
        Assert.Equal(A.Correct, c.Action);
        Assert.True(c.CharCodeChanged);
        Assert.False(c.PublishShapes);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-a-19")]
    public void Shrunk_body_type_and_unread_shapes_are_not_changes()
    {
        var shrunk = new R("gen2", LastChar, LastShapes);   // a strict subset of "bibo,gen2"
        Assert.Equal(A.None, Run(shrunk, shrunk).Action);
        var unread = new R(LastBody, LastChar, null);
        Assert.Equal(A.None, Run(unread, unread).Action);
        var empty = new R(LastBody, LastChar, "");   // read: nothing enabled — a real change from shp_a
        Assert.Equal(A.Correct, Run(empty, empty).Action);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-7")]
    public void The_loop_only_samples_and_keeps_every_side_effect()
    {
        var src = File.ReadAllText(Path.Combine(ProteusDir(), "Services", "CompositorService.PostRedraw.cs"));
        int at = src.IndexOf("private void SchedulePostRedrawBodyTypeCheck(", System.StringComparison.Ordinal);
        int end = src.IndexOf("internal static PostSettleVerdict DecidePostSettle(", System.StringComparison.Ordinal);
        Assert.True(at >= 0 && end > at);
        var loop = src[at..end];
        Assert.Contains("DecidePostSettle(", loop);
        Assert.DoesNotContain("IsStrictSubsetKey(", loop);
        Assert.DoesNotContain("materialsIncoherent", loop);
        foreach (var effect in new[]
                 {
                     "_activeMtrlSnapshot = snapshot;", "_activeMtrlSnapshotDirty = false;", "_activeMtrlSnapshotRewalk = false;",
                     "config.CachedActiveMaterialPaths = snapshot.ToList();", "if (verdict.PublishShapes) _bodyShapeSnapshot = latest.Shapes;",
                     "_activeMtrlSnapshotRewalk = true;", "TriggerRecomposite(\"post-settle-correction\", force: false);",
                 })
            Assert.Contains(effect, loop);
    }

    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");
}
