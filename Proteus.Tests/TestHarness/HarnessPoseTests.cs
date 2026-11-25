using System;
using System.Collections.Generic;
using Proteus.Interop;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The pose routes' pure part: the yaw convention, the clamp, the put-back tolerances and decisions, labels, the
/// pinned emote rows; and the pose dump's log line keeping the folder out.</summary>
public class HarnessPoseTests
{
    [Fact]
    [Trait("Plan", "AC-knee-standoff-9")]
    public void Yaw_maps_to_the_cameras_heading()
    {
        // DirH = facing + yaw (pinned by the in-game yaw-180 check), wrapped; YawOf is its inverse.
        Assert.Equal(1.0f, HarnessPose.DirHFor(1.0f, 0), 5);
        Assert.Equal(HarnessPose.Wrap(1.0f + MathF.PI), HarnessPose.DirHFor(1.0f, 180), 5);
        Assert.Equal(HarnessPose.Wrap(1.0f + MathF.PI / 2), HarnessPose.DirHFor(1.0f, 90), 5);
        foreach (var yaw in new[] { -170f, -90f, 0f, 45f, 179f })
            Assert.Equal(yaw, HarnessPose.YawOf(2.5f, HarnessPose.DirHFor(2.5f, yaw)), 3);
        Assert.InRange(HarnessPose.DirHFor(3.0f, 359), -MathF.PI, MathF.PI);
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-23")]
    public void Requests_are_clamped_to_the_games_limits()
    {
        var now = new CameraNow(0.3f, -0.2f, 5f, 1.5f, 20f, -1.4f, 0.8f);
        var (pose, clamped) = HarnessPose.Target(now, 0f, null, 60f, 0.5f);
        Assert.Equal(0.3f, pose.DirH);                 // yaw not given: kept
        Assert.Equal(0.8f, pose.DirV, 5);
        Assert.Equal(1.5f, pose.Distance);
        Assert.Equal(["pitch", "distance"], clamped);
        var (inside, none) = HarnessPose.Target(now, 0f, 90f, -10f, 7f);
        Assert.Empty(none);
        Assert.Equal(7f, inside.Distance);
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-23")]
    public void Put_back_uses_the_settle_tolerance()
    {
        var left = new CameraPose(1f, -0.2f, 6f);
        CameraNow At(float h, float v, float d) => new(h, v, d, 1.5f, 20f, -1.4f, 0.8f);
        Assert.True(HarnessPose.StillLeft(left, At(1.015f, -0.19f, 6.04f)));
        Assert.False(HarnessPose.StillLeft(left, At(1.03f, -0.2f, 6f)));
        Assert.False(HarnessPose.StillLeft(left, At(1f, -0.2f, 6.1f)));
        // Across the wrap: π and -π are the same heading.
        Assert.True(HarnessPose.StillLeft(new CameraPose(MathF.PI - 0.005f, 0, 5), At(-MathF.PI + 0.005f, 0, 5)));
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-22")]
    public void The_emote_is_stood_up_only_while_the_harness_s_loop_still_plays()
    {
        var rec = new EmoteRecord(52, "groundsit", HarnessPose.LoopMode, 52, true, DateTime.UtcNow);
        Assert.Equal("send", HarnessPose.EmoteDecision(rec, new EmoteNow(HarnessPose.LoopMode, 52)));
        Assert.Equal("changed", HarnessPose.EmoteDecision(rec, new EmoteNow(HarnessPose.NormalMode, 0)));
        Assert.Equal("changed", HarnessPose.EmoteDecision(rec, new EmoteNow(HarnessPose.LoopMode, 50)));
        Assert.Equal("changed", HarnessPose.EmoteDecision(rec, null));
        Assert.True(HarnessPose.InTransition(new EmoteNow("EmoteSitting", 52), rec));
        Assert.False(HarnessPose.InTransition(new EmoteNow(HarnessPose.NormalMode, 0), rec));
    }

    [Theory]
    [InlineData("idle", true)]
    [InlineData("groundsit-2", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData("Idle", false)]
    [InlineData("a b", false)]
    [InlineData("abcdefghijabcdefghijabcdefghijabc", false)]
    [InlineData(null, false)]
    [Trait("Plan", "EDGE-knee-standoff-24")]
    public void Labels_are_plain(string? label, bool ok) => Assert.Equal(ok, HarnessPose.ValidLabel(label));

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-21")]
    public void The_pinned_rows_must_match_the_sheet()
    {
        Assert.True(HarnessPose.RowsMatch(new Dictionary<ushort, string> { [50] = "/sit", [52] = "/groundsit" }));
        Assert.False(HarnessPose.RowsMatch(new Dictionary<ushort, string> { [50] = "/sit" }));
        Assert.False(HarnessPose.RowsMatch(new Dictionary<ushort, string> { [50] = "/sit", [52] = "/lounge" }));
        Assert.False(HarnessPose.RowsMatch(null));
    }

    [Fact]
    [Trait("Plan", "AC-knee-standoff-10")]
    [Trait("Plan", "EDGE-knee-standoff-24")]
    public void The_folder_stays_out_of_the_log_when_asked()
    {
        const string dir = @"C:\Users\someone\AppData\Roaming\XIVLauncher\pluginConfigs\Proteus\test-harness\posedumps\s\idle";
        var hidden = LivePoseDump.Line(120, 201, 7, dir, logFolder: false);
        Assert.DoesNotContain(dir, hidden);
        Assert.DoesNotContain("someone", hidden);
        Assert.EndsWith("-> harness posedump folder", hidden);
        Assert.EndsWith(dir, LivePoseDump.Line(120, 201, 7, dir, logFolder: true));   // the owner's /proteus posedump unchanged
    }
}
