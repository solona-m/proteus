using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// The rim lift's fold guard judged against the face as authored (<see cref="BodyRetarget.Tuning.NoRimTurnGuard"/>): a
/// face the earlier passes have tilted can be lifted past a right angle from its author's way without ever flipping
/// within one lift, and the solve's fold count would show it.
/// </summary>
public class RimTurnGuardTests
{
    /// <summary>The bump: 2.2 mm high, 10 mm across, at the origin.</summary>
    private static float Bump(float x, float y)
    {
        float r = MathF.Sqrt(x * x + y * y) / 0.01f;
        return r >= 1f ? 0f : 0.0022f * (1f - r * r) * (1f - r * r);
    }

    private static ModelParts Model(string material, float[] positions, float[] normals, int[] triangles)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < positions.Length / 3; v++)
        {
            var at = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return new ModelParts
        {
            Positions = positions,
            Normals = normals,
            MeshSpans = [new MeshSpan(0, 0, positions.Length / 3)],
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "0.0", Material = material, Triangles = triangles,
                    Ordinals = Enumerable.Range(0, triangles.Length / 3).ToArray(), AttributeMask = 0, Min = min, Max = max,
                    Toggleable = true,
                },
            ],
            AttributeNames = [],
            Min = min,
            Max = max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    /// <summary>A flat body the refit grows the bump into, as the drawn body.</summary>
    private static BodyRetarget.TargetBody Drawn()
    {
        const int count = 81;
        const float step = 0.0025f, half = (count - 1) * step / 2f;
        var flat = new List<float>();
        var grown = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        for (int i = 0; i < count; i++)
        for (int k = 0; k < count; k++)
        {
            float x = i * step - half, y = k * step - half;
            flat.AddRange([x, y, 0f]);
            grown.AddRange([x, y, Bump(x, y)]);
            nrm.AddRange([0f, 0f, 1f]);
        }
        for (int i = 0; i + 1 < count; i++)
        for (int k = 0; k + 1 < count; k++)
        {
            int a = i * count + k, b = a + count, c = a + 1, d = b + 1;
            tris.AddRange([a, b, d, a, d, c]);
        }
        var before = Model("/mt_c0201b0001_bibo.mtrl", flat.ToArray(), nrm.ToArray(), tris.ToArray());
        var after = Model("/mt_c0201b0001_bibo.mtrl", grown.ToArray(), nrm.ToArray(), tris.ToArray());
        Assert.True(IdentityCorrespondence.TryBuild(before, after, "legs", out var grow, out string why), why);
        return BodyRetarget.TargetBody.Build([new BodyRetarget.SlotPair("_dwn", grow!, after)], null, null, before: false);
    }

    /// <summary>
    /// A strip of cloth: its rim on the skin at x = ±15 mm, spanning the bump; its far row authored 4 mm up and 0.5 mm
    /// out — a steep wall — but laid by the passes before almost flat, 4 mm out and 0.5 mm up, and held there. Lifting
    /// the rim clear of the bump turns the strip past a right angle from its author's way, yet never flips it against
    /// where it lies.
    /// </summary>
    private static (BodyRetarget.Sets Sets, Vec3[] Delta, bool[] Stay) Strip()
    {
        float[] at = [-0.015f, 0f, 0f, 0.015f, 0f, 0f, -0.015f, 0.0005f, 0.004f, 0.015f, 0.0005f, 0.004f];
        float[] normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
        var strip = Model("/mt_c0201e6255_top_a.mtrl", at, normals, [0, 1, 3, 0, 3, 2]);
        var sets = BodyRetarget.Sets.From(strip);
        var delta = new Vec3[sets.NodeCount];
        var stay = new bool[sets.NodeCount];
        foreach (int v in new[] { 2, 3 })
        {
            delta[sets.NodeOf[v]] = new Vec3(0f, 0.0035f, -0.0035f);
            stay[sets.NodeOf[v]] = true;
        }
        Assert.Equal(0, BodyRetarget.FoldedTriangles(sets, delta));   // tilted, not yet turned over
        return (sets, delta, stay);
    }

    [Fact]
    [Trait("Plan", "AC-skirt-hips-3")]
    public void The_rim_lift_never_turns_a_face_past_its_authors_way()
    {
        var drawn = Drawn();

        var (oldSets, oldDelta, oldStay) = Strip();
        int oldLifted = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRimTurnGuard: true),
                                                () => BodyRetarget.LiftRims(oldSets, drawn, oldDelta, oldStay));
        // As before 1261: lifted, and turned over against its author's way.
        Assert.True(oldLifted > 0, "the harness should lift the rim");
        Assert.True(BodyRetarget.FoldedTriangles(oldSets, oldDelta) > 0, "the harness should reproduce the turn past a right angle");

        var (sets, delta, stay) = Strip();
        BodyRetarget.LiftRims(sets, drawn, delta, stay);
        Assert.Equal(0, BodyRetarget.FoldedTriangles(sets, delta));
    }
}
