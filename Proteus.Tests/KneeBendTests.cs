using System;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>The knee standoff's real poses (<see cref="KneeBend"/>): the owner's dump read, mirrored, and knee bends scaled
/// from it with the hip held.</summary>
public class KneeBendTests(ITestOutputHelper o)
{
    [LocalDataFact(LocalData.IdlePose)]
    [Trait("Plan", "AC-knee-standoff-1")]
    public void A_scaled_bend_adds_the_asked_flexion_and_holds_the_hip()
    {
        var idle = KneeBend.Load(LocalData.Path(LocalData.IdlePose));
        var dumped = KneeBend.Angles(idle, "r");
        foreach (float added in new[] { 15f, 30f, 60f, 90f })
        {
            var bent = KneeBend.Scaled(idle, added);
            foreach (var side in new[] { "l", "r" })
            {
                var a = KneeBend.Angles(bent, side);
                o.WriteLine($"+{added} {side}: added {a.Added:F1}; hip {a.Hip:F1}; j_asi_b {a.KneeB:F1} j_asi_c {a.KneeC:F1}");
                Assert.InRange(a.Added, added - 1f, added + 1f);
                // The knee bones only: their split kept within a degree of the dump's proportion.
                Assert.InRange(a.KneeB / Math.Max(1e-3f, a.KneeB + a.KneeC), dumped.KneeB / (dumped.KneeB + dumped.KneeC) - 0.05f,
                               dumped.KneeB / (dumped.KneeB + dumped.KneeC) + 0.05f);
            }
            // The base leg's hip is the dump's, within 0.1°.
            Assert.InRange(KneeBend.Angles(bent, "r").Hip, dumped.Hip - 0.1f, dumped.Hip + 0.1f);
        }
    }

    [LocalDataFact(LocalData.IdlePose)]
    [Trait("Plan", "AC-knee-standoff-1")]
    public void Mirroring_swaps_the_legs()
    {
        var idle = KneeBend.Load(LocalData.Path(LocalData.IdlePose));
        var mirrored = KneeBend.Mirrored(idle);
        var (l, r) = (KneeBend.Angles(idle, "l"), KneeBend.Angles(idle, "r"));
        var (ml, mr) = (KneeBend.Angles(mirrored, "l"), KneeBend.Angles(mirrored, "r"));
        Assert.Equal(r.KneePose, ml.KneePose, 1);
        Assert.Equal(l.KneePose, mr.KneePose, 1);
        Assert.Equal(r.Hip, ml.Hip, 1);
    }
}
