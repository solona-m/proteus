using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>The knee standoff's real poses (<see cref="KneeBend"/>): the owner's dumps read, mirrored, and knee bends scaled
/// from them with the hip held.</summary>
public class KneeBendTests(ITestOutputHelper o)
{
    public static TheoryData<string> Dumps => new(LocalData.IdlePose, LocalData.IdleFreshPose);

    private static bool Present(string need) => LocalData.Missing([need]) == null;

    [LocalDataTheory(LocalData.IdlePose)]
    [MemberData(nameof(Dumps))]
    [Trait("Plan", "AC-knee-standoff-1")]
    public void A_scaled_bend_adds_the_asked_flexion_and_changes_only_the_knee(string dump)
    {
        if (!Present(dump)) return;   // the owner's local dumps: skipped elsewhere
        var idle = KneeBend.Load(LocalData.Path(dump));
        var dumped = KneeBend.Angles(idle, "r");
        foreach (float added in new[] { 15f, 30f, 60f, 90f })
        {
            var bent = KneeBend.Scaled(idle, added);
            foreach (var side in new[] { "l", "r" })
            {
                var a = KneeBend.Angles(bent, side);
                o.WriteLine($"{dump} +{added} {side}: added {a.Added:F2}; hip {a.Hip:F2}; j_asi_b {a.KneeB:F2} j_asi_c {a.KneeC:F2}");
                Assert.InRange(a.Added, added - 0.5f, added + 0.5f);
                // Only the knee bones, in the dump's split.
                float split = a.KneeB / (a.KneeB + a.KneeC), dumpedSplit = dumped.KneeB / (dumped.KneeB + dumped.KneeC);
                Assert.InRange(split, dumpedSplit - 0.05f, dumpedSplit + 0.05f);
            }
            Assert.InRange(KneeBend.Angles(bent, "r").Hip, dumped.Hip - 0.1f, dumped.Hip + 0.1f);   // the hip held

            // Every bone outside the two legs as dumped; the base leg above the knee as dumped too.
            var leftLeg = Below(idle, "j_asi_a_l");
            var rightKnee = Below(idle, "j_asi_b_r");
            for (int b = 0; b < idle.BoneCount; b++)
            {
                if (leftLeg.Contains(b) || rightKnee.Contains(b)) continue;
                Assert.True(Near(idle.Pose(b), bent.Pose(b)), $"{idle.BoneName(b)} moved");
            }
        }
    }

    private static HashSet<int> Below(LivePose p, string root)
    {
        var set = new HashSet<int>();
        for (int b = 0; b < p.BoneCount; b++)
            for (string? n = p.BoneName(b); n != null; n = p.ParentOf(n))
                if (n == root) { set.Add(b); break; }
        return set;
    }

    private static bool Near(in Matrix4x4 a, in Matrix4x4 b)
    {
        var d = a - b;
        return new[] { d.M11, d.M12, d.M13, d.M21, d.M22, d.M23, d.M31, d.M32, d.M33, d.M41, d.M42, d.M43 }.All(x => MathF.Abs(x) < 1e-4f);
    }

    [LocalDataTheory(LocalData.IdlePose)]
    [MemberData(nameof(Dumps))]
    [Trait("Plan", "AC-knee-standoff-1")]
    public void Mirroring_swaps_the_legs(string dump)
    {
        if (!Present(dump)) return;
        var idle = KneeBend.Load(LocalData.Path(dump));
        var mirrored = KneeBend.Mirrored(idle);
        var (l, r) = (KneeBend.Angles(idle, "l"), KneeBend.Angles(idle, "r"));
        var (ml, mr) = (KneeBend.Angles(mirrored, "l"), KneeBend.Angles(mirrored, "r"));
        Assert.Equal(r.KneePose, ml.KneePose, 1);
        Assert.Equal(l.KneePose, mr.KneePose, 1);
        Assert.Equal(r.Hip, ml.Hip, 1);
    }

    [LocalDataFact(LocalData.IdlePose)]
    [Trait("Plan", "EDGE-knee-standoff-27")]
    [Trait("Plan", "EDGE-knee-standoff-29")]
    public void The_recorded_idle_and_sit_choices_hold()
    {
        // 7a: the fresh idle differs from coldcase2 by more than 5° at a knee or hip, so it is the primary idle.
        if (Present(LocalData.IdlePose) && Present(LocalData.IdleFreshPose))
        {
            var cold = KneeBend.Load(LocalData.Path(LocalData.IdlePose));
            var fresh = KneeBend.Load(LocalData.Path(LocalData.IdleFreshPose));
            float most = new[] { "l", "r" }.Max(side =>
            {
                var (a, b) = (KneeBend.Angles(cold, side), KneeBend.Angles(fresh, side));
                return MathF.Max(MathF.Abs(a.Added - b.Added), MathF.Abs(a.Hip - b.Hip));
            });
            o.WriteLine($"idle-fresh against coldcase2: {most:F1}° at most");
            Assert.True(most > 5f, "the fresh idle is within 5° of coldcase2: the recorded choice (fresh primary) no longer holds");
        }
        // 7b: no sit or groundsit dump was captured (the idle camera refused the emote); nothing to drop yet.
        Assert.False(Present(LocalData.SitPose) && !Present(LocalData.GroundsitPose), "a sit dump without its groundsit to compare");
    }

    [LocalDataFact(LocalData.IdlePose)]
    public void A_dump_without_a_flexed_knee_is_refused()
    {
        // A leg half a degree beyond bind gives no axis to scale: refused rather than a runaway or flipped bend.
        var idle = KneeBend.Load(LocalData.Path(LocalData.IdlePose));
        Assert.Throws<System.IO.InvalidDataException>(() => KneeBend.Scaled(KneeBend.Scaled(idle, 0.5f), 15));
    }
}
