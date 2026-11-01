using System;
using System.IO;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Body size panel's wiring that ImGui can't drive from a test, pinned in its source (as OtherRaceWearerTests
/// does). The behaviour itself is checked in game.
/// </summary>
public class SourceBodyPickerTests
{
    private static readonly string Panel =
        File.ReadAllText(SourceFile("Proteus/Gui/BodyRetargetPanel.cs")).Replace("\r\n", "\n");

    /// <summary>The body of the method whose declaration starts with <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string signature)
    {
        int at = Panel.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature + " not found");
        int open = Panel.IndexOf('{', at), depth = 0;
        for (int i = open; i < Panel.Length; i++)
        {
            if (Panel[i] == '{') depth++;
            else if (Panel[i] == '}' && --depth == 0) return Panel[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced " + signature);
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-1")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-1")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-3")]
    public void Made_for_pick_takes_the_shown_refit_down()   // [AC-gh33-36-37-body-size-1] [EDGE-gh33-36-37-body-size-1] [EDGE-gh33-36-37-body-size-3]
    {
        string picker = Method("private void DrawSourceBodyPicker(in RetargetContext ctx)");
        Assert.Contains("DropPlan(ctx);", picker);
        Assert.DoesNotContain("planned = null", picker);
        Assert.DoesNotContain("pendingPreview = null", picker);
        Assert.Contains("DrawSourceBodyPicker(ctx);", Panel);

        // DropPlan does nothing without a plan (no preview the panel doesn't own is ended), and takes the shown one down.
        string drop = Method("private void DropPlan(in RetargetContext ctx)");
        Assert.StartsWith("{\n        if (planned == null) return;", drop);
        Assert.Contains("pendingPreview = null;", drop);
        Assert.Contains("ctx.EndPreview();", drop);
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-4")]
    [Trait("Plan", "AC-gh33-36-37-body-size-6")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-19")]
    public void Picks_and_sizes_reach_the_memory()   // [AC-gh33-36-37-body-size-4] [AC-gh33-36-37-body-size-6] [EDGE-gh33-36-37-body-size-19]
    {
        // The services are tested on their own; this pins that the panel calls them where it records and fills.
        Assert.Contains("RememberedTargets.PickedBody(config, GarmentPart(ctx), dir);",
                        Method("private void PickOntoBody(in RetargetContext ctx, string dir)"));
        Assert.Contains("RememberedTargets.ChoseSize(config, GarmentPart(ctx), bodyDir, slot,",
                        Method("private void Remember(in RetargetContext ctx, string slot)"));
        Assert.Contains("RememberedTargets.Fillable(config, bodyDir,",
                        Method("private void ApplyRemembered(in RetargetContext ctx)"));
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-16")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-21")]
    public void The_restore_runs_after_the_race_and_before_anything_decided_for_the_body()   // [EDGE-gh33-36-37-body-size-16] [EDGE-gh33-36-37-body-size-21]
    {
        // After the race is known (it decides whether a body sizes the garment); ahead of the remembered and worn sizes;
        // and a switch returns, so nothing below is decided for the old body.
        string draw = Method("public void Draw(in RetargetContext given)");
        int race = draw.IndexOf("OnRaceChanged(ctx);", StringComparison.Ordinal);
        int restore = draw.IndexOf("RestoreForGarment(ctx))", StringComparison.Ordinal);
        int remembered = draw.IndexOf("ApplyRemembered(ctx);", StringComparison.Ordinal);
        int worn = draw.IndexOf("PresetWornTargets(ctx);", StringComparison.Ordinal);
        Assert.True(race >= 0 && restore > race && remembered > restore && worn > remembered);
        Assert.Matches(@"RestoreForGarment\(ctx\)\)\s*\{\s*DrawSaved\(ctx\);\s*return;", draw[restore..]);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-24")]
    public void A_restore_reuses_the_cached_catalog()   // [EDGE-gh33-36-37-body-size-24]
    {
        // No freshness claimed: the cache is what was read since the last body scan (a known limitation).
        Assert.Contains("SwitchOnto(ctx, dir, CachedCatalog(dir));", Method("private bool RestoreForGarment(in RetargetContext ctx)"));
        Assert.Contains("BodySizeCatalog.Read(root)", Method("private void PickOntoBody(in RetargetContext ctx, string dir)"));
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-25")]
    public void A_restore_judges_ends_as_cleared()   // [EDGE-gh33-36-37-body-size-25]
    {
        string clear = Method("public void Clear()");
        Assert.Contains("keepEnds = true;", clear);
        Assert.Contains("endsUnreadable = false;", clear);
        Assert.Contains("bodyRestoredFor = null;", clear);
        Assert.Contains("LimbUsableAfter(d, part)", Method("private bool RestoreForGarment(in RetargetContext ctx)"));
        Assert.Contains("bodyRestoredFor != GarmentKey(ctx) && RestoreForGarment(ctx)", Panel);
    }

    private static string SourceFile(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }
}
