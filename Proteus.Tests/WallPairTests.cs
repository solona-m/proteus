using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// A thick shell's two walls (see <see cref="BodyRetarget.FaceCheck.WallPartner"/>): which points pair, the walls moved
/// together when the skin is cleared out of a face (<see cref="BodyRetarget.ClearFaces"/>), and the inner wall taken for
/// the cover over what lies beneath it (<see cref="BodyRetarget.CoveredPoints"/>, <see cref="BodyRetarget.KeepLayerOrder"/>).
/// </summary>
public class WallPairTests
{
    private const string Cloth = "/mt_c0201e6255_top_a.mtrl";
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";

    /// <summary>One flat sheet: <paramref name="count"/> × <paramref name="count"/> points <paramref name="step"/> apart,
    /// centred on (<paramref name="cx"/>, <paramref name="cy"/>) at height <paramref name="z"/>, facing up or down.</summary>
    private sealed record Sheet(int Count, float Step, float Z, bool Up, float Cx = 0f, float Cy = 0f);

    /// <summary>
    /// Sheets in one model and one material. <paramref name="rims"/> joins sheet pairs along their x = min edge, making
    /// them one connected piece — a thick shell's walls meet at its rims.
    /// </summary>
    private static ModelParts Build(string material, Sheet[] sheets, params (int A, int B)[] rims)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        var starts = new List<int>();
        foreach (var s in sheets)
        {
            int start = pos.Count / 3;
            starts.Add(start);
            float half = (s.Count - 1) * s.Step / 2f;
            for (int i = 0; i < s.Count; i++)
            for (int k = 0; k < s.Count; k++)
            {
                pos.AddRange([s.Cx + i * s.Step - half, s.Cy + k * s.Step - half, s.Z]);
                nrm.AddRange([0f, 0f, s.Up ? 1f : -1f]);
            }
            for (int i = 0; i + 1 < s.Count; i++)
            for (int k = 0; k + 1 < s.Count; k++)
            {
                int a = start + i * s.Count + k, b = a + s.Count, c = a + 1, d = b + 1;
                if (s.Up) tris.AddRange([a, b, d, a, d, c]);
                else tris.AddRange([a, d, b, a, c, d]);
            }
        }
        foreach (var (x, y) in rims)
            for (int k = 0; k + 1 < sheets[x].Count; k++)
            {
                int a = starts[x] + k, b = starts[x] + k + 1, c = starts[y] + k, d = starts[y] + k + 1;
                tris.AddRange([a, c, d, a, d, b]);
            }
        var p = pos.ToArray();
        var t = tris.ToArray();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < p.Length / 3; v++)
        {
            var at = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return new ModelParts
        {
            Positions = p,
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, p.Length / 3)],
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "0.0", Material = material, Triangles = t,
                    Ordinals = Enumerable.Range(0, t.Length / 3).ToArray(), AttributeMask = 0, Min = min, Max = max,
                    Toggleable = true,
                },
            ],
            AttributeNames = [],
            Min = min,
            Max = max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    private static int[] Partners(ModelParts m)
    {
        var sets = BodyRetarget.Sets.From(m);
        var cloth = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) cloth[n] = true;
        return BodyRetarget.WallPartners(sets, cloth);
    }

    private const int Fine = 21;
    private const float FineStep = 0.005f;

    [Fact]
    [Trait("Plan", "AC-skirt-hips-1")]
    public void A_thick_shells_walls_pair_point_for_point()
    {
        // Inner wall 3 mm off the skin facing in, outer wall 5 mm off facing out, joined at a rim: 2 mm thick, as
        // Geometric's skirt is.
        var shell = Build(Cloth, [new(Fine, FineStep, 0.003f, false), new(Fine, FineStep, 0.005f, true)], (0, 1));
        var partners = Partners(shell);
        int n = Fine * Fine;
        for (int v = 0; v < n; v++)
        {
            Assert.Equal(n + v, partners[v]);
            Assert.Equal(v, partners[n + v]);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-skirt-hips-9")]
    public void A_lining_is_no_wall()
    {
        // A lining faces the way its shell does: never paired, so the layer guard still keeps it behind the shell.
        var lined = Build(Cloth, [new(Fine, FineStep, 0.003f, true), new(Fine, FineStep, 0.005f, true)], (0, 1));
        Assert.All(Partners(lined), p => Assert.Equal(-1, p));
    }

    [Fact]
    [Trait("Plan", "AC-skirt-hips-1")]
    public void A_small_closed_piece_is_no_thick_shell()
    {
        // A bead, a chain link, a stud: two sides as near as a thick shell's walls, but too small a piece.
        var bead = Build(Cloth, [new(5, FineStep, 0.003f, false), new(5, FineStep, 0.005f, true)], (0, 1));
        Assert.True(bead.Positions.Length / 3 < BodyRetarget.WallPieceLeast);
        Assert.All(Partners(bead), p => Assert.Equal(-1, p));
    }

    [Fact]
    [Trait("Plan", "AC-skirt-hips-2")]
    public void A_layer_beneath_never_takes_the_outer_walls_place()
    {
        // A stocking 2 mm under the inner wall — as near as the outer wall is — but its own piece: the inner wall still
        // pairs with its own outer wall.
        var garment = Build(Cloth, [new(Fine, FineStep, 0.001f, true), new(Fine, FineStep, 0.003f, false),
                                    new(Fine, FineStep, 0.005f, true)], (1, 2));
        var partners = Partners(garment);
        int n = Fine * Fine;
        for (int v = 0; v < n; v++)
        {
            Assert.Equal(-1, partners[v]);
            Assert.Equal(2 * n + v, partners[n + v]);
        }
    }

    [Fact]
    [Trait("Plan", "AC-skirt-hips-2")]
    [Trait("Plan", "EDGE-skirt-hips-13")]
    public void What_lies_beneath_a_thick_shell_is_covered_by_its_inner_wall()
    {
        var garment = Build(Cloth, [new(Fine, FineStep, 0.001f, true), new(Fine, FineStep, 0.003f, false),
                                    new(Fine, FineStep, 0.005f, true)], (1, 2));
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var oneWay = BodyRetarget.FacesOneWay(garment, sets);
        var stocking = Enumerable.Range(0, Fine * Fine).Select(v => sets.NodeOf[v]).ToHashSet();

        var on = BodyRetarget.CoveredPoints(sets, layered, oneWay);
        var why = new Dictionary<int, string>();
        var off = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoWallCover: true),
                                          () => BodyRetarget.CoveredPoints(sets, layered, oneWay, why: (n, w) => why[n] = w));

        Assert.True(on.Count(c => stocking.Contains(c.Node)) > stocking.Count / 2, "the stocking should be covered by the inner wall");
        Assert.True(off.Count(c => stocking.Contains(c.Node)) == 0, "as before 1261, an inner wall facing the other way covers nothing");
        // The diagnostic says why: the cover it met faced the other way.
        Assert.True(stocking.Count(n => why.GetValueOrDefault(n) == "facing") > stocking.Count / 2);
    }

    [Fact]
    [Trait("Plan", "EDGE-skirt-hips-8")]
    public void Lifting_the_inner_wall_off_a_covered_point_carries_the_outer_wall()
    {
        // The stocking has come up through the inner wall; with no room to go back (lift only), the cover goes out —
        // both its walls, so the inner never comes through the outer, and nothing turns over.
        var garment = Build(Cloth, [new(Fine, FineStep, 0.001f, true), new(Fine, FineStep, 0.003f, false),
                                    new(Fine, FineStep, 0.004f, true)], (1, 2));
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered, BodyRetarget.FacesOneWay(garment, sets));
        int n = Fine * Fine;
        var delta = new Vec3[sets.NodeCount];
        for (int v = 0; v < n; v++) delta[sets.NodeOf[v]] = new Vec3(0f, 0f, 0.003f);
        float Z(int v) => garment.Positions[v * 3 + 2] + delta[sets.NodeOf[v]].Z;

        BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered, drawn: null, liftOnly: true);

        int middle = Fine / 2 * Fine + Fine / 2;
        Assert.Equal(0.004f, Z(middle), 4);                                    // the stocking stays where it was put
        Assert.True(Z(n + middle) > Z(middle), $"inner wall {Z(n + middle) * 1000f:F2} mm over a stocking at {Z(middle) * 1000f:F2}");
        for (int v = 0; v < n; v++)
            Assert.True(Z(2 * n + v) >= Z(n + v) - 1e-5f, $"point {v}: outer wall {Z(2 * n + v) * 1000f:F2} mm, inner {Z(n + v) * 1000f:F2}");
        Assert.Equal(0, BodyRetarget.FoldedTriangles(sets, delta));
    }

    // ── the walls moved together when the skin is cleared out of a face ──────────────────────────────────────────────

    /// <summary>The bump: 2.2 mm high, 10 mm across, under the middle of one of the shell's 40 mm faces.</summary>
    private static float Bump(float x, float y)
    {
        float r = MathF.Sqrt(x * x + y * y) / 0.01f;
        return r >= 1f ? 0f : 0.0022f * (1f - r * r) * (1f - r * r);
    }

    /// <summary>A thick shell (inner wall 1.5 mm off the skin facing in, outer 2.5 mm facing out, joined at a rim) of
    /// 40 mm faces over a flat body that the refit grows a bump into, under the middle of a face.</summary>
    private static (ModelParts Shell, List<BodyRetarget.SlotPair> Pairs) Bumped()
    {
        var flat = Build(Skin, [new(81, 0.0025f, 0f, true)]);
        var grown = (float[])flat.Positions.Clone();
        for (int v = 0; v < grown.Length / 3; v++) grown[v * 3 + 2] = Bump(grown[v * 3], grown[v * 3 + 1]);
        var target = new ModelParts
        {
            Positions = grown, Normals = flat.Normals, MeshSpans = flat.MeshSpans, Parts = flat.Parts,
            AttributeNames = flat.AttributeNames, Min = flat.Min, Max = flat.Max with { Z = 0.0022f },
            ShatteredSubmeshes = flat.ShatteredSubmeshes,
        };
        Assert.True(IdentityCorrespondence.TryBuild(flat, target, "legs", out var grow, out string why), why);
        // Faces 40 mm on a side; the grid offset so the bump is in the middle of a face, off its diagonal.
        var shell = Build(Cloth, [new(13, 0.04f, 0.0015f, false, 0.02f, 0.012f), new(13, 0.04f, 0.0025f, true, 0.02f, 0.012f)],
                          (0, 1));
        return (shell, [new BodyRetarget.SlotPair("_dwn", grow!, target)]);
    }

    /// <summary>How many of the shell's faces the bump comes through the middle of, after the refit.</summary>
    private static int Through(ModelParts shell, BodyRetarget.Solved solved)
    {
        int count = 0;
        var t = shell.Parts[0].Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            Vector3 P(int v) => new Vector3(shell.Positions[v * 3], shell.Positions[v * 3 + 1], shell.Positions[v * 3 + 2])
                                + new Vector3(solved.Edit.DeltaAt(v).X, solved.Edit.DeltaAt(v).Y, solved.Edit.DeltaAt(v).Z);
            // Sampled across the face: the bump is a tenth of its size.
            var (a, b, c) = (P(t[i]), P(t[i + 1]), P(t[i + 2]));
            bool through = false;
            for (int u = 0; u <= 20 && !through; u++)
            for (int w = 0; u + w <= 20 && !through; w++)
            {
                var s = a + (b - a) * (u / 20f) + (c - a) * (w / 20f);
                through = s.Z < Bump(s.X, s.Y) - 1e-5f;
            }
            if (through) count++;
        }
        return count;
    }

    [Fact]
    [Trait("Plan", "AC-skirt-hips-1")]
    public void Clearing_the_skin_out_of_a_face_moves_both_walls()
    {
        var (shell, pairs) = Bumped();
        var traced = new List<string>();
        BodyRetarget.FaceTrace.Value = (k, why) => traced.Add(why.StartsWith("crossed", StringComparison.Ordinal) ? $"{why}@{k}" : why);
        var paired = BodyRetarget.Solve(shell, pairs);
        BodyRetarget.FaceTrace.Value = null;
        var alone = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoWallPair: true), () => BodyRetarget.Solve(shell, pairs));

        // Pushed alone, the inner wall crossed the outer and the layer guard put it back; moved together, it cleared.
        Assert.True(Through(shell, alone) > 0, $"the harness should reproduce the faces left through: alone {Through(shell, alone)}, "
                                              + $"paired {Through(shell, paired)}, pushed {alone.Pushed}, worst {alone.WorstMove * 1000f:F2} mm, "
                                              + $"reasons {string.Join(",", traced.Distinct())}");
        Assert.True(Through(shell, paired) == 0, $"paired {Through(shell, paired)}, alone {Through(shell, alone)}; reasons {string.Join(",", traced.Distinct())}");
        int n = 169;
        for (int v = 0; v < n; v++)
        {
            float inner = shell.Positions[v * 3 + 2] + paired.Edit.DeltaAt(v).Z;
            float outer = shell.Positions[(n + v) * 3 + 2] + paired.Edit.DeltaAt(n + v).Z;
            Assert.True(outer > inner, $"point {v}: outer wall {outer * 1000f:F2} mm, inner {inner * 1000f:F2}");
        }
        // The diagnostic saw it pushed.
        Assert.Contains("pushed", traced);
    }

    [Fact]
    [Trait("Plan", "AC-skirt-hips-1")]
    public void A_wall_whose_partner_cannot_move_is_not_pushed_through_it()
    {
        // The outer wall held where its author put it: the inner wall may not be carried — or let — through it.
        var (shell, pairs) = Bumped();
        // A kept-shape piece: in the push-out, so paired, but staying where it was put — the partner the carry cannot move.
        // Each outer-wall point near the bump its own one-point piece: kept where it is, nothing else about it changed.
        List<IReadOnlyCollection<int>> kept = Enumerable.Range(169, 169)
            .Where(v => MathF.Abs(shell.Positions[v * 3]) < 0.07f && MathF.Abs(shell.Positions[v * 3 + 1]) < 0.07f)
            .Select(v => (IReadOnlyCollection<int>)[v]).ToList();
        var solved = BodyRetarget.Solve(shell, pairs, keepShape: kept);
        for (int v = 0; v < 169; v++)
        {
            float inner = shell.Positions[v * 3 + 2] + solved.Edit.DeltaAt(v).Z;
            float outer = shell.Positions[(169 + v) * 3 + 2] + solved.Edit.DeltaAt(169 + v).Z;
            Assert.True(outer > inner, $"point {v}: outer wall {outer * 1000f:F2} mm, inner {inner * 1000f:F2}");
        }
    }
}
