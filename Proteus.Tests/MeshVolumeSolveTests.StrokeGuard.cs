using System;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The guard at the end of a brush stroke with "Move skin too" (neolithe-skin-move-fails): a stroke reshapes the skin, so
/// its riders are judged along the skin's turned way out at the stroke's start and end — lifted back to the clearance
/// the stroke found them at (the author's floor at most), never loosened, never thrown off where the skin's normals
/// disagree with its faces. Measured inside the solve.
/// </summary>
public partial class MeshVolumeSolveTests
{
    /// <summary>Cloth vertex <paramref name="cloth"/>'s height over skin vertex <paramref name="skinVertex"/>, along the
    /// skin's drawn normal as it now stands.</summary>
    private static float HeightOver(MeshVolumeSolve solve, float[] after, int cloth, int skinVertex)
    {
        var n = solve.NormalAt(skinVertex);
        var way = Vector3.Normalize(new Vector3(n.X, n.Y, n.Z));
        return Vector3.Dot(At(after, cloth) - At(after, skinVertex), way);
    }

    /// <summary>
    /// A regression guard for strokes: no rider of the skin a stroke moved is left under its floor along the skin's turned
    /// way out. (The measuring axis that failed on Neolithe L needs irregular real topology to show; on a regular grid
    /// the face rule never lifts less — that proof is the LocalData sculpt test.)
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-neolithe-skin-move-fails-1")]
    public void A_stroke_leaves_no_rider_of_moved_skin_under_its_floor()
    {
        var l = Layered(21, 0.001f, clothStride: 2);
        var solve = SkinSolve(l.Model);
        var inputs = solve.FollowInputs;
        var binding = SkinFollow.Bind(inputs);
        var vertexOf = new System.Collections.Generic.Dictionary<int, int>();
        for (int v = 0; v < inputs.NodeOf.Length; v++) vertexOf.TryAdd(inputs.NodeOf[v], v);

        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.03f, 0.006f);
        solve.EndStroke();
        var after = solve.Positions();
        int judged = 0;
        foreach (int v in l.ClothAt)
        {
            int node = inputs.NodeOf[v];
            if (binding.BindWeight(node) <= 0f) continue;
            var way = Vector3.Zero;
            var under = Vector3.Zero;
            bool moved = false;
            foreach (var (s, w) in binding.CornersOf(node))
            {
                int sv = vertexOf[s];
                var n = solve.NormalAt(sv);
                way += new Vector3(n.X, n.Y, n.Z) * w;
                under += At(after, sv) * w;
                moved |= Moved(l.Model, after, sv).Length() > 1e-5f;
            }
            if (!moved) continue;
            judged++;
            float h = Vector3.Dot(At(after, v) - under, Vector3.Normalize(way));
            float floor = MathF.Min(0.001f, binding.AuthoredHeight(node));
            Assert.True(h >= floor - 4e-5f, $"cloth vertex {v}: {h * 1000f:F3} mm over its skin, floor {floor * 1000f:F3} mm");
        }
        Assert.True(judged > 0, "the stroke moved no skin a rider rides");
        Assert.Equal(0, solve.RidersLeftUnder);
        Assert.Equal(0, Folded(l.Model, after));
        Assert.Equal(0, SkinPoke.MadeWorse(l.Model, after));
    }

    [Theory]
    [Trait("Plan", "AC-neolithe-skin-move-fails-2")]
    [Trait("Plan", "EDGE-neolithe-skin-move-fails-8")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stroke_does_not_loosen_cloth_the_user_tightened(bool mirror)
    {
        var l = Layered(21, 0.001f, centred: mirror);
        var solve = SkinSolve(l.Model);
        // The user pulls a patch of cloth down onto the skin, the skin left where it is (no guard runs).
        Assert.True(solve.BeginMove(Cells(l.ClothAt, 9, 11, 9, 11), adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, -0.0007f, 0f));
        Assert.True(solve.EndMove());
        float tightened = HeightOver(solve, solve.Positions(), l.ClothAt[10, 10], l.SkinAt[10, 10]);
        Assert.InRange(tightened, 0.00029f, 0.00031f);

        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.003f, mirror: mirror);
        solve.EndStroke();
        var after = solve.Positions();
        float now = HeightOver(solve, after, l.ClothAt[10, 10], l.SkinAt[10, 10]);
        Assert.True(At(after, l.SkinAt[10, 10]).Y > 0.0018f, "the stroke moved the skin");
        Assert.True(now >= tightened - 1e-5f, $"{now * 1000f:F3} mm: under where the stroke found it");
        Assert.True(now < 0.0009f, $"{now * 1000f:F3} mm: the stroke loosened the cloth the user tightened back toward 1 mm");
        Assert.Equal(0, Folded(l.Model, after));
    }

    [Fact]
    [Trait("Plan", "EDGE-neolithe-skin-move-fails-12")]
    public void A_stroke_keeps_a_tight_authored_clearance()
    {
        var l = Layered(21, 0.0003f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.003f);
        solve.EndStroke();
        var after = solve.Positions();
        float h = HeightOver(solve, after, l.ClothAt[10, 10], l.SkinAt[10, 10]);
        Assert.True(h >= 0.00029f, $"{h * 1000f:F3} mm over the bulge's top");
        Assert.Equal(0, SkinPoke.MadeWorse(l.Model, after));
        Assert.Equal(0, Folded(l.Model, after));
        Assert.Equal(0, solve.RidersLeftUnder);
    }

    [Fact]
    [Trait("Plan", "AC-neolithe-skin-move-fails-4")]
    [Trait("Plan", "EDGE-neolithe-skin-move-fails-13")]
    public void A_small_stroke_on_split_normal_skin_lifts_nothing_its_turn_does_not_explain()
    {
        // Skin normals ±40° off its faces: along such a normal the cloth measures cos 40° × 1 mm, under the author's
        // floor, so an author-relative measure would lift every rider ~0.23 mm on the slightest stroke. A 0.5 mm stroke
        // turns the skin under a degree, which sinks nothing.
        var l = SplitNormalLayers();
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[5, 5]), 0.06f, 0.0005f);
        solve.EndStroke();
        var after = solve.Positions();
        Assert.True(solve.LastGuard.WorstMm <= 0.02f, $"{solve.LastGuard}");
        // Inside the skin's open edges (30 mm of fade, where the skin holds and the brush moves the cloth alone).
        for (int i = 3; i <= 7; i++)
        for (int j = 3; j <= 7; j++)
        {
            float cloth = Moved(l.Model, after, l.ClothAt[i, j]).Length(), skinMove = Moved(l.Model, after, l.SkinAt[i, j]).Length();
            Assert.True(cloth <= skinMove + 2e-5f, $"cloth [{i},{j}] moved {cloth * 1000f:F3} mm over its skin's {skinMove * 1000f:F3} mm");
        }
    }
}
