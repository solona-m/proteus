using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A model's names all live in one string block, grouped by kind: attributes, then bones, then materials, then
/// shapes. Measured across every model on disk — 2987 of 2996 with all of the first three are in that order, and
/// the nine that were not came from this plugin; 1064 of 1064 with shapes put them after the materials.
/// <para/>
/// The grouping is load-bearing because a reader that walks the block as a LIST — Lumina, and so Penumbra and
/// TexTools — assigns the first attributeCount strings to attributes and the next boneCount to bones. Put a name
/// in the wrong group and that reader takes a bone name for an attribute and misnames every bone after it: a
/// refit came out of TexTools with its sleeve weighted to a bone three places along, because the writer put the
/// bones first and the model declared three attributes.
/// <para/>
/// So any writer that ADDS a name has to put it in its own group, not merely somewhere in the block.
/// </summary>
public class ModelStringBlockOrderTests
{
    private const string Material = "/mt_c0201e0485_top_a.mtrl";

    private static byte[] Model(params string[] attrs) => SyntheticModel.Build(
        attrs,
        [new SyntheticModel.Mesh(Material,
            new SyntheticModel.Sub(0, Weights: [("j_hij_l", 0.5f), ("j_sebo_c", 0.5f)]))],
        SyntheticModel.V6,
        shapeNames: ["shp_base"]);

    /// <summary>The block sliced the way a reader that trusts the count does.</summary>
    private static string[] Slice(byte[] mdl)
    {
        var p = SecondSkinWriter.Parse(mdl);
        uint count = BitConverter.ToUInt32(p.S, p.DeclEnd);
        var names = new List<string>();
        int pos = 0;
        for (int i = 0; i < count && pos < p.StrSize; i++)
        {
            int end = pos;
            while (end < p.StrSize && mdl[p.StrBlock + end] != 0) end++;
            names.Add(Encoding.ASCII.GetString(mdl, p.StrBlock + pos, end - pos));
            pos = end + 1;
        }
        return [.. names];
    }

    private static void AssertGrouped(byte[] mdl)
    {
        var p = SecondSkinWriter.Parse(mdl);
        var sliced = Slice(mdl);
        Assert.DoesNotContain("", sliced);

        int At(string n) => Array.IndexOf(sliced, n);
        foreach (var n in p.AttrNames.Concat(p.BoneNames).Concat(p.MatNames))
            Assert.True(At(n) >= 0, $"'{n}' is not reachable by walking the block");

        // Each group sits whole, in order: the LAST of one before the FIRST of the next.
        Assert.True(p.AttrNames.Max(At) < p.BoneNames.Min(At),
            $"attributes end at {p.AttrNames.Max(At)}, bones start at {p.BoneNames.Min(At)}");
        Assert.True(p.BoneNames.Max(At) < p.MatNames.Min(At),
            $"bones end at {p.BoneNames.Max(At)}, materials start at {p.MatNames.Min(At)}");
        if (p.Shapes.Count > 0)
            Assert.True(p.MatNames.Max(At) < p.Shapes.Keys.Min(At),
                $"materials end at {p.MatNames.Max(At)}, shapes start at {p.Shapes.Keys.Min(At)}");

        // And a reader walking in order lands on the same names the offsets give.
        Assert.Equal(p.AttrNames, sliced.Take(p.AttrNames.Length));
        Assert.Equal(p.BoneNames, sliced.Skip(p.AttrNames.Length).Take(p.BoneNames.Length));
    }

    /// <summary>The fixture itself, so the tests below are not measured against an unreal layout.</summary>
    [Fact]
    public void The_fixture_groups_its_names()
        => AssertGrouped(Model("atr_hij", "atr_nek", "atr_ude"));

    /// <summary>
    /// The case that broke. A new attribute parked at the END of the block sits after the bones, the materials and
    /// the shapes, so the walk takes the first bone name as the fourth attribute and every bone reads one along.
    /// </summary>
    [Fact]
    public void An_added_attribute_joins_the_other_attributes()
    {
        var before = Model("atr_hij", "atr_nek", "atr_ude");
        var after = ModelAttributeWriter.AddAttribute(before, "atr_top", [(0, 0)]);

        var p = SecondSkinWriter.Parse(after);
        Assert.Equal(["atr_hij", "atr_nek", "atr_ude", "atr_top"], p.AttrNames);
        AssertGrouped(after);

        // The other names still say what they said, and the submesh got the new bit.
        var b = SecondSkinWriter.Parse(before);
        Assert.Equal(b.BoneNames, p.BoneNames);
        Assert.Equal(b.MatNames, p.MatNames);
        Assert.Equal(b.Shapes.Keys, p.Shapes.Keys);
        Assert.Equal(1u << 3, BitConverter.ToUInt32(after, p.SubmeshStart + 8) & (1u << 3));
    }

    /// <summary>A model with no attributes yet: the new one starts the group rather than trailing the block.</summary>
    [Fact]
    public void The_first_attribute_starts_the_group()
    {
        var after = ModelAttributeWriter.AddAttribute(Model(), "atr_top", [(0, 0)]);
        Assert.Equal(["atr_top"], SecondSkinWriter.Parse(after).AttrNames);
        AssertGrouped(after);
    }

    /// <summary>
    /// Shapes come last, so a shape name appended at the end of the block is already in its own group. This pins
    /// that, since the same writer adds both and only one of them needed moving.
    /// </summary>
    [Fact]
    public void An_added_shape_stays_in_the_shape_group()
    {
        var before = Model("atr_hij", "atr_nek");
        var moved = new Dictionary<int, IReadOnlyDictionary<int, System.Numerics.Vector3>>
            { [0] = new Dictionary<int, System.Numerics.Vector3> { [0] = new(0.01f, 0.02f, 0.03f) } };
        var after = ModelAttributeWriter.AddShape(before, "shp_hib", moved);

        var p = SecondSkinWriter.Parse(after);
        Assert.Equal(["shp_base", "shp_hib"], p.Shapes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        AssertGrouped(after);
    }
}
