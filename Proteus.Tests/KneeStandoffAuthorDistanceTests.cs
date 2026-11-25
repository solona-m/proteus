using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The knee standoff's distance from the author's own size (decision 11): points the knee pass moved are each
/// judged against the knob within its budget, every other point as before.</summary>
public partial class SkirtHipsRegressionLocalTests
{
    /// <summary>Each of the author's cloth points: how far the nearest cloth point of <paramref name="refit"/> is (50 mm at most).</summary>
    internal static float[] FromAuthorPerPoint(ModelParts author, ModelParts refit)
    {
        static List<Vector3> Cloth(ModelParts m)
        {
            var seen = new HashSet<int>();
            var points = new List<Vector3>();
            foreach (var part in m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                foreach (int v in part.Triangles)
                    if (seen.Add(v)) points.Add(new Vector3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]));
            return points;
        }
        var theirs = Cloth(refit);
        return Cloth(author).Select(p => theirs.Count == 0 ? 0.05f : MathF.Min(0.05f, theirs.Min(q => Vector3.Distance(p, q)))).ToArray();
    }

    [LocalDataFact(Geometric, Bibo)]
    [Trait("Plan", "AC-knee-standoff-5")]
    [Trait("Suite", "Diagnostic")]
    [Trait("Speed", "Slow")]
    public void Geometric_s_knee_moves_stay_within_their_budget_from_the_author()
    {
        var r = Refit(Geometric, "leg size/small/chara/equipment/e6010/model/c0201e6010_dwn.mdl",
                      "leg size/large/chara/equipment/e6010/model/c0201e6010_dwn.mdl", "_dwn", [Bibo],
                      now: new BodyRetarget.Tuning(), before: new BodyRetarget.Tuning(NoKneeStandoff: true), fitted: false);
        var now = FromAuthorPerPoint(r.Author, r.Now.Model);
        var knob = FromAuthorPerPoint(r.Author, r.Before.Model);
        int further = now.Zip(knob).Count(x => x.First > x.Second + 1e-5f);
        output.WriteLine($"Geometric Small -> Large: {further} of {now.Length} author points further from the knee model, "
                         + $"most {now.Zip(knob).Max(x => x.First - x.Second) * 1000f:F2} mm; folded {r.Before.Folded} -> {r.Now.Folded}");
        foreach (var (n, k) in now.Zip(knob))
            Assert.True(n <= k + BodyRetarget.KneeBudget + 1e-5f, $"{n * 1000:F2} mm from the author's own, knob {k * 1000:F2}");
        Assert.True(r.Now.Folded <= r.Before.Folded, $"{r.Now.Folded} folded, {r.Before.Folded} with the knob");
        Assert.True(r.Now.Inside <= r.Before.Inside, $"{r.Now.Inside} inside, {r.Before.Inside} with the knob");
    }
}
