using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// A piece of cloth the author covered with another stays behind it. Reported on a tank top over a bra: after a refit
/// onto a larger breast the bra came through the top across the front.
/// </summary>
public class LayerOrderTests
{
    private const float Gap = 0.003f;
    private const float Step = 0.005f;
    private const int Side = 5;

    /// <summary>Two flat sheets facing +Z, the second <see cref="Gap"/> in front of the first: two pieces, one model.</summary>
    private static ModelParts Layered(string innerMaterial, string outerMaterial)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var inner = new List<int>();
        var outer = new List<int>();
        for (int layer = 0; layer < 2; layer++)
        {
            int start = pos.Count / 3;
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                {
                    pos.AddRange([x * Step, y * Step, layer * Gap]);
                    nrm.AddRange([0f, 0f, 1f]);
                }
            var tris = layer == 0 ? inner : outer;
            for (int y = 0; y + 1 < Side; y++)
                for (int x = 0; x + 1 < Side; x++)
                {
                    int a = start + y * Side + x;
                    tris.AddRange([a, a + 1, a + Side + 1, a, a + Side + 1, a + Side]);
                }
        }
        var p = pos.ToArray();
        ModelPart Part(int sub, string material, int[] tris) => new()
        {
            Mesh = 0, Submesh = sub, Island = -1, Label = $"1.{sub + 1}", Material = material, Triangles = tris,
            Ordinals = Enumerable.Range(0, tris.Length / 3).ToArray(), AttributeMask = 0,
            Min = Vector3.Zero, Max = new Vector3(Side * Step, Side * Step, Gap), Toggleable = true,
        };
        return new ModelParts
        {
            Positions = p,
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, p.Length / 3)],
            Parts = [Part(0, innerMaterial, inner.ToArray()), Part(1, outerMaterial, outer.ToArray())],
            AttributeNames = [],
            Min = Vector3.Zero,
            Max = new Vector3(Side * Step, Side * Step, Gap),
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    /// <summary>The middle point of the inner sheet, as a vertex.</summary>
    private const int Middle = (Side / 2) * Side + Side / 2;

    private static Vec3[] ThroughTheTop(BodyRetarget.Sets sets)
    {
        // Every point carried 10 mm out with the body; the middle of the inner sheet 5 mm further, through the outer.
        var delta = new Vec3[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++) delta[n] = new Vec3(0f, 0f, 0.01f);
        delta[sets.NodeOf[Middle]] = new Vec3(0f, 0f, 0.015f);
        return delta;
    }

    [Fact]
    public void A_covered_point_brought_through_its_cover_goes_back_behind_it()
    {
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered);
        Assert.Contains(covered, c => c.Node == sets.NodeOf[Middle]);

        var delta = ThroughTheTop(sets);
        var outerBefore = Enumerable.Range(Side * Side, Side * Side).Select(v => delta[sets.NodeOf[v]]).ToArray();
        BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered);

        // Behind the outer sheet (which is now at 3 + 10 mm), by the kept gap; the inner sheet was authored at 0.
        float z = delta[sets.NodeOf[Middle]].Z;
        Assert.True(z <= Gap + 0.01f - BodyRetarget.LayerOrderGap + 1e-5f, $"inner middle at {z * 1000:F2} mm");
        // The cover is the piece seen, and stays where it was put.
        Assert.Equal(outerBefore, Enumerable.Range(Side * Side, Side * Side).Select(v => delta[sets.NodeOf[v]]));
    }

    [Fact]
    public void Layers_that_kept_their_order_are_left_alone()
    {
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var delta = new Vec3[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++) delta[n] = new Vec3(0f, 0f, 0.01f);
        var was = (Vec3[])delta.Clone();

        Assert.Equal(0, BodyRetarget.KeepLayerOrder(sets, BodyRetarget.CoveredPoints(sets, layered), delta, null, layered));
        Assert.Equal(was, delta);
    }

    [Fact]
    public void A_point_facing_in_is_not_covered_by_the_layer_beneath_it()
    {
        // The outer sheet faces IN, as a lining's inside does: along its normal lies the inner sheet, which is beneath
        // it, not over it. Kept "behind" that, it would be pushed outward.
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        for (int v = Side * Side; v < 2 * Side * Side; v++) garment.Normals[v * 3 + 2] = -1f;
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered, BodyRetarget.FacesOneWay(garment, sets));

        var outer = Enumerable.Range(Side * Side, Side * Side).Select(v => sets.NodeOf[v]).ToHashSet();
        Assert.DoesNotContain(covered, c => outer.Contains(c.Node));
    }

    [Fact]
    public void A_body_mods_alternatives_are_not_layers()
    {
        // Two pubic hair meshes on the same skin, one switched on at a time: keeping one behind the other would sink
        // whichever is drawn into the skin.
        var garment = Layered("/mt_c0201b0001_bibopube.mtrl", "/mt_c0201b0001_betterpube.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        Assert.Empty(BodyRetarget.CoveredPoints(sets, layered));

        var delta = ThroughTheTop(sets);
        var was = (Vec3[])delta.Clone();
        BodyRetarget.KeepLayerOrder(sets, BodyRetarget.CoveredPoints(sets, layered), delta, null, layered);
        Assert.Equal(was, delta);
    }
}
