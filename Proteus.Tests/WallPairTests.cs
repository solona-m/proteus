using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A thick shell's two walls (see <see cref="BodyRetarget.FaceCheck.WallPartner"/>): which points pair, and the inner
/// wall taken for the cover over what lies beneath it (see <see cref="BodyRetarget.CoveredPoints"/>).
/// </summary>
public class WallPairTests
{
    private const string Cloth = "/mt_c0201e6255_top_a.mtrl";
    private const float Step = 0.005f;

    /// <summary>Square sheets of <paramref name="count"/> × <paramref name="count"/> points, one per (height, facing up?).</summary>
    private static ModelParts Sheets(int count, params (float Z, bool Up)[] sheets)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        float half = (count - 1) * Step / 2f;
        foreach (var (z, up) in sheets)
        {
            int start = pos.Count / 3;
            for (int i = 0; i < count; i++)
            for (int k = 0; k < count; k++)
            {
                pos.AddRange([i * Step - half, k * Step - half, z]);
                nrm.AddRange([0f, 0f, up ? 1f : -1f]);
            }
            for (int i = 0; i + 1 < count; i++)
            for (int k = 0; k + 1 < count; k++)
            {
                int a = start + i * count + k, b = a + count, c = a + 1, d = b + 1;
                // Wound to face the way the normal does.
                if (up) tris.AddRange([a, b, d, a, d, c]);
                else tris.AddRange([a, d, b, a, c, d]);
            }
        }
        var p = pos.ToArray();
        var t = tris.ToArray();
        var min = new Vector3(-half, -half, sheets.Min(s => s.Z));
        var max = new Vector3(half, half, sheets.Max(s => s.Z));
        return new ModelParts
        {
            Positions = p,
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, p.Length / 3)],
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "0.0", Material = Cloth, Triangles = t,
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

    [Fact]
    [Trait("Plan", "AC-skirt-hips-1")]
    public void A_thick_shells_walls_pair_point_for_point()
    {
        // Inner wall 3 mm off the skin facing in, outer wall 5 mm off facing out: Geometric's skirt, 2 mm thick.
        var shell = Sheets(21, (0.003f, false), (0.005f, true));
        var partners = Partners(shell);
        int n = 21 * 21;
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
        Assert.All(Partners(Sheets(21, (0.003f, true), (0.005f, true))), p => Assert.Equal(-1, p));
    }

    [Fact]
    [Trait("Plan", "EDGE-skirt-hips-5")]
    public void A_small_closed_piece_is_no_thick_shell()
    {
        // A bead, a chain link, a stud: two sides as near as a thick shell's walls, but too small a piece.
        var bead = Sheets(5, (0.003f, false), (0.005f, true));
        Assert.True(bead.Positions.Length / 3 < BodyRetarget.WallPieceLeast);
        Assert.All(Partners(bead), p => Assert.Equal(-1, p));
    }

    [Fact]
    [Trait("Plan", "AC-skirt-hips-2")]
    [Trait("Plan", "EDGE-skirt-hips-13")]
    public void What_lies_beneath_a_thick_shell_is_covered_by_its_inner_wall()
    {
        // A stocking 0.5 mm off the skin under the skirt's inner wall at 3 mm (facing in) and outer wall at 4.5 mm. (The
        // inner wall pairs with the wall nearest it facing the other way: a stocking nearer it than its own outer wall
        // would take that place — on Geometric's skirt the walls are nearer each other than the stocking is.)
        var garment = Sheets(21, (0.0005f, true), (0.003f, false), (0.0045f, true));
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var oneWay = BodyRetarget.FacesOneWay(garment, sets);
        int n = 21 * 21;
        bool StockingCovered(IEnumerable<BodyRetarget.Covered> covered)
            => covered.Count(c => sets.NodeOf.Take(n).Contains(c.Node)) > n / 2;

        var on = BodyRetarget.CoveredPoints(sets, layered, oneWay);
        var off = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoWallCover: true),
                                          () => BodyRetarget.CoveredPoints(sets, layered, oneWay));

        Assert.True(StockingCovered(on), "the stocking should be covered by the inner wall");
        Assert.False(StockingCovered(off), "as before 1258, an inner wall facing the other way covers nothing");
    }
}
