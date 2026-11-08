using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Proteus.Gui;
using Proteus.TestHarness;
using Xunit;
using Facts = Proteus.TestHarness.HarnessStudio.StudioFacts;

namespace Proteus.Tests.TestHarness;

/// <summary>The Studio routes' pure parts: body validation, the drag's matrix, the model match and the put-back plan.</summary>
public class HarnessStudioTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    [Trait("Plan", "AC-move-all-parts-13")]
    public void ParseSet_AcceptsEveryField()
    {
        var (r, error) = HarnessStudio.ParseSet(Body("""
            {"tool":"rotate","mode":"all","view":"model","mirror":true,"adjacent":false,"moveSkin":true,
             "unlockAll":true,"lock":["1.1"],"unlock":["1.2"],"part":"1.3"}
            """));
        Assert.Null(error);
        Assert.Equal(("rotate", "all", "model"), (r!.Tool, r.Mode, r.View));
        Assert.Equal((true, false, true), (r.Mirror, r.Adjacent, r.MoveSkin));
        Assert.True(r.UnlockAll);
        Assert.Equal(["1.1"], r.Lock);
        Assert.Equal(["1.2"], r.Unlock);
        Assert.Equal("1.3", r.Part);
        // The order they are applied in is the panel's, fixed: the fields list names it.
        Assert.Equal(["tool", "mode", "view", "mirror", "adjacent", "falloffMm", "moveSkin", "unlockAll", "lock", "unlock", "part"], HarnessStudio.SetFields);
        Assert.DoesNotContain("drawer", HarnessStudio.SetFields);
        Assert.Null(r.FalloffMm);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-50")]
    public void ParseSet_FalloffMm_in_the_sliders_range()
    {
        // #86: QA reproduces a Whole-parts drag with the falloff it was made with — the slider's 1–300 mm.
        Assert.Equal(50f, HarnessStudio.ParseSet(Body("""{"falloffMm":50}""")).Request!.FalloffMm);
        Assert.Equal(1f, HarnessStudio.ParseSet(Body("""{"falloffMm":1}""")).Request!.FalloffMm);
        Assert.Equal(300f, HarnessStudio.ParseSet(Body("""{"falloffMm":300}""")).Request!.FalloffMm);
        foreach (var bad in new[] { """{"falloffMm":0.5}""", """{"falloffMm":301}""", """{"falloffMm":"50"}""", """{"falloffMm":null}""" })
            Assert.NotNull(HarnessStudio.ParseSet(Body(bad)).Error);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-50")]
    public void FalloffMm_reported_set_and_put_back()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(DefaultMatcapsDownloadServiceTests.RepoRoot(), "Proteus", "Gui", "PartsPanel.Harness.cs"));
        Assert.Contains("falloffMm = moveFalloffMm, moveSkin }", src);
        Assert.Contains("falloffMm = AdjacentNow ? moveFalloffMm : 0f,", src);
        Assert.Contains("if (request.FalloffMm is { } falloff && falloff != moveFalloffMm) { moveFalloffMm = falloff; ForgetMoveFalloff(); }", src);
        Assert.Contains("if (moveFalloffMm != snap.FalloffMm) { moveFalloffMm = snap.FalloffMm; ForgetMoveFalloff(); }", src);
    }

    [Theory]
    [Trait("Plan", "EDGE-move-all-parts-23")]
    [InlineData("""{}""")]
    [InlineData("""{"tool":"inflate"}""")]
    [InlineData("""{"mode":"some"}""")]
    [InlineData("""{"mirror":"yes"}""")]
    [InlineData("""{"unlockAll":false}""")]
    [InlineData("""{"lock":[]}""")]
    [InlineData("""{"lock":["1.1"],"unlock":["1.1"]}""")]
    [InlineData("""{"part":""}""")]
    [InlineData("""{"drawer":true}""")]
    [InlineData("""{"falloffMm":1000}""")]
    public void ParseSet_Rejects(string json) => Assert.NotNull(HarnessStudio.ParseSet(Body(json)).Error);

    [Fact]
    [Trait("Plan", "AC-move-all-parts-14")]
    public void ParseDrag_Kinds()
    {
        var move = HarnessStudio.ParseDrag(Body("""{"offsetMm":[10,0,-5]}""")).Request!;
        Assert.Equal("move", move.Kind);
        Assert.Equal(new Vector3(0.01f, 0f, -0.005f), move.Offset);
        var turn = HarnessStudio.ParseDrag(Body("""{"axis":[0,0,2],"degrees":45}""")).Request!;
        Assert.Equal(("rotate", Vector3.UnitZ, 45f), (turn.Kind, turn.Axis, turn.Degrees));
        Assert.Equal(Vector3.UnitY, HarnessStudio.ParseDrag(Body("""{"axis":"y","degrees":-90}""")).Request!.Axis);
        Assert.Equal(new Vector3(1.5f), HarnessStudio.ParseDrag(Body("""{"factor":1.5}""")).Request!.Factors);
        Assert.Equal(new Vector3(1f, 2f, 0.5f), HarnessStudio.ParseDrag(Body("""{"factors":[1,2,0.5]}""")).Request!.Factors);
        Assert.Equal("undo", HarnessStudio.ParseDrag(Body("""{"undo":true}""")).Request!.Kind);
    }

    [Theory]
    [Trait("Plan", "EDGE-move-all-parts-23")]
    [InlineData("""{}""")]
    [InlineData("""{"offsetMm":[1001,0,0]}""")]
    [InlineData("""{"offsetMm":[1,2]}""")]
    [InlineData("""{"offsetMm":["a",0,0]}""")]
    [InlineData("""{"axis":"w","degrees":10}""")]
    [InlineData("""{"axis":[0,0,0],"degrees":10}""")]
    [InlineData("""{"axis":"x"}""")]
    [InlineData("""{"axis":"x","degrees":361}""")]
    [InlineData("""{"factor":0.05}""")]
    [InlineData("""{"factor":11}""")]
    [InlineData("""{"factors":[1,1,20]}""")]
    [InlineData("""{"factor":2,"factors":[1,1,1]}""")]
    [InlineData("""{"undo":true,"offsetMm":[1,0,0]}""")]
    [InlineData("""{"undo":false}""")]
    [InlineData("""{"offsetMm":[1,0,0],"factor":2}""")]
    [InlineData("""{"nudge":1}""")]
    public void ParseDrag_Rejects(string json) => Assert.NotNull(HarnessStudio.ParseDrag(Body(json)).Error);

    [Fact]
    [Trait("Plan", "AC-move-all-parts-14")]
    public void DragTransform_IsTheGizmosMatrix()
    {
        var pivot = new Vector3(0.1f, 1.2f, -0.3f);
        var turn = HarnessStudio.ParseDrag(Body("""{"axis":"y","degrees":30}""")).Request!;
        Assert.Equal(RotateGizmo.About(pivot, Vector3.UnitY, 30f * MathF.PI / 180f), HarnessStudio.DragTransform(turn, pivot));
        var scale = HarnessStudio.ParseDrag(Body("""{"factors":[1,2,3]}""")).Request!;
        Assert.Equal(PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 3f)), HarnessStudio.DragTransform(scale, pivot));
        var even = HarnessStudio.ParseDrag(Body("""{"factor":1.25}""")).Request!;
        Assert.Equal(PartScaleDrag.Build(pivot, new Vector3(1.25f)), HarnessStudio.DragTransform(even, pivot));
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-34")]
    public void MatchModel()
    {
        var models = new List<(string, string)>
        {
            ("S/top.mdl", "chara/equipment/e0001/model/c0201e0001_top.mdl"),
            ("M/top.mdl", "chara/equipment/e0001/model/c0201e0001_top.mdl"),
            ("legs.mdl", "chara/equipment/e0001/model/c0201e0001_dwn.mdl"),
        };
        Assert.Equal((0, (string?)null), HarnessStudio.MatchModel(models, "s\\TOP.mdl"));
        Assert.Equal((2, (string?)null), HarnessStudio.MatchModel(models, "CHARA\\equipment\\e0001\\model\\c0201e0001_dwn.mdl"));
        Assert.Equal((-1, "model-ambiguous"), HarnessStudio.MatchModel(models, "chara/equipment/e0001/model/c0201e0001_top.mdl"));
        Assert.Equal((-1, "model-not-in-mod"), HarnessStudio.MatchModel(models, "nope.mdl"));
    }

    // ── put back ────────────────────────────────────────────────────────────

    private static Facts Good(bool harnessModel = true) => new(
        new Dictionary<string, string?> { ["top.mdl"] = "AA" }, new Dictionary<string, string> { ["top.mdl"] = "AA" },
        harnessModel, EditVersionNow: 5, EditVersionLast: 5, SaveOwed: false, DragOpen: false, ApplyingSizes: false);

    public static TheoryData<string, string, string, string?> PutBackRows => new()
    {
        { "all-good", "revert", "restore", null },
        { "not-harness-saved", "changed", "changed", "not-harness-saved" },
        { "hash-changed", "changed", "changed", "hash-changed" },
        { "unreadable", "changed", "changed", "hash-changed" },
        { "model-edited", "changed", "changed", "model-edited" },
        { "save-owed", "changed", "changed", "save-owed" },
        { "drag-open", "changed", "changed", "drag-open" },
        { "applying-sizes", "changed", "changed", "applying-sizes" },
        { "other-model", "revert", "changed", null },
        { "other-model-touched", "changed", "changed", "hash-changed" },
        { "nothing-patched", "none", "restore", null },
        { "nothing-patched-other", "none", "changed", null },
    };

    [Theory]
    [MemberData(nameof(PutBackRows))]
    [Trait("Plan", "AC-move-all-parts-15")]
    [Trait("Plan", "EDGE-move-all-parts-21")]
    [Trait("Plan", "EDGE-move-all-parts-39")]
    public void PlanPutBack_Table(string row, string files, string panel, string? code)
    {
        var f = Good();
        f = row switch
        {
            "not-harness-saved" => f with { Patched = new Dictionary<string, string?> { ["top.mdl"] = "AA", ["user.mdl"] = "BB" } },
            "hash-changed" => f with { Patched = new Dictionary<string, string?> { ["top.mdl"] = "CC" } },
            "unreadable" => f with { Patched = new Dictionary<string, string?> { ["top.mdl"] = null } },
            "model-edited" => f with { EditVersionNow = 6 },
            "save-owed" => f with { SaveOwed = true },
            "drag-open" => f with { DragOpen = true },
            "applying-sizes" => f with { ApplyingSizes = true },
            "other-model" => Good(harnessModel: false),
            "other-model-touched" => Good(harnessModel: false) with { Patched = new Dictionary<string, string?> { ["top.mdl"] = "CC" } },
            "nothing-patched" => f with { Patched = new Dictionary<string, string?>() },
            "nothing-patched-other" => Good(harnessModel: false) with { Patched = new Dictionary<string, string?>() },
            _ => f,
        };
        var plan = HarnessStudio.PlanPutBack(f);
        Assert.Equal((files, panel, code), (plan.Files, plan.Panel, plan.Code));
        // With the panel on something else, a revert reloads the session mod by name and leaves the panel alone.
        Assert.Equal(row == "other-model", plan.ReloadByName);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    public void SessionRecordsHashAndEditVersion()
    {
        var s = new StudioSession("Outfit", "top.mdl", "snap", windowOpened: false) { EditVersion = 3 };
        s.Record([new StudioFile("top.mdl", "AA")], 4);
        Assert.Equal("AA", s.Files["top.mdl"]);
        Assert.Equal(4, s.EditVersion);
        s.Record([new StudioFile("TOP.mdl", "BB"), new StudioFile("mat.mtrl", "CC")], 7);
        Assert.Equal("BB", s.Files["top.mdl"]);   // a later save replaces the hash, any case
        Assert.Equal(2, s.Files.Count);
        Assert.Equal(7, s.EditVersion);
    }
}
