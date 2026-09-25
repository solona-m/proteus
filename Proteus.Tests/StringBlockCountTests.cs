using System;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A model's names — bones, attributes, materials — all live in one string block, and every name is referenced by a
/// byte OFFSET into it. The block is preceded by a count and a size.
/// <para/>
/// The game reads a name at its offset, so the count is nothing to it. Every other reader slices the block into a
/// LIST of that many strings first and then resolves offsets through the list: Lumina does, so Penumbra and TexTools
/// do. The writer left the count at zero, which made that list empty, and so every bone, attribute and material came
/// back as the empty string. Penumbra called the materials invalid and refused to export the model — "Armature does
/// not contain bone" — and a refit could not be opened in any authoring tool, while the game drew it correctly.
/// </summary>
public class StringBlockCountTests
{
    private const string Material = "/mt_c0201e0485_top_a.mtrl";

    private static byte[] Host() => SyntheticModel.Build(
        ["atr_top", "atr_ude"],
        [new SyntheticModel.Mesh(Material,
            new SyntheticModel.Sub(0, Weights: [("j_ude_b_l", 0.6f), ("n_hte_l", 0.4f)]))],
        SyntheticModel.V6);

    /// <summary>The host rebuilt through the writer, as a refit does it: the model under test.</summary>
    private static byte[] Rebuilt()
    {
        var plan = new (string Bone, float W)[]?[] { [("n_hte_l", 1f)], null, null };
        return SecondSkinWriter.Build([], [], Host(), out _, hostReskin: m => m == 0 ? plan : null);
    }

    /// <summary>The count and size a reader finds ahead of the block, and where the block starts.</summary>
    private static (uint Count, uint Size, int At) Block(byte[] mdl)
    {
        var p = SecondSkinWriter.Parse(mdl);
        return (BitConverter.ToUInt32(p.S, p.DeclEnd), p.StrSize, p.StrBlock);
    }

    /// <summary>Slice the block the way a reader that trusts the count does.</summary>
    private static string[] SliceByCount(byte[] mdl)
    {
        var (count, size, at) = Block(mdl);
        var names = new string[count];
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            int end = pos;
            while (end < size && mdl[at + end] != 0) end++;
            names[i] = System.Text.Encoding.ASCII.GetString(mdl, at + pos, end - pos);
            pos = end + 1;
        }
        return names;
    }

    /// <summary>
    /// The case that broke. Build a shell and read its names the way Penumbra does — every name the model uses has
    /// to be in that list.
    /// </summary>
    [Fact]
    public void Every_name_is_reachable_by_slicing_the_block_by_its_count()
    {
        var shell = Rebuilt();
        var parsed = SecondSkinWriter.Parse(shell);
        var sliced = SliceByCount(shell);

        Assert.DoesNotContain("", sliced);
        foreach (var bone in parsed.BoneNames) Assert.Contains(bone, sliced);
        foreach (var attr in parsed.AttrNames) Assert.Contains(attr, sliced);
        foreach (var mat in parsed.MatNames) Assert.Contains(mat, sliced);
    }

    /// <summary>
    /// The count must be exactly the names — not the NULs in the block, which the four-byte padding inflates, and
    /// not a guess. A reader walks the list in order, so one too many leaves it reading into the padding.
    /// </summary>
    [Fact]
    public void The_count_is_the_number_of_names_and_no_more()
    {
        var shell = Rebuilt();
        var parsed = SecondSkinWriter.Parse(shell);
        var (count, size, at) = Block(shell);

        Assert.Equal((uint)(parsed.BoneNames.Length + parsed.AttrNames.Length + parsed.MatNames.Count), count);

        // And the last name ends at or before the block's declared end, with only padding after it.
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            while (pos < size && shell[at + pos] != 0) pos++;
            pos++;
        }
        Assert.True(pos <= size, $"the {count} names run {pos - size} byte(s) past the {size}-byte block");
        for (int k = pos; k < size; k++)
            Assert.Equal(0, shell[at + k]);
    }

    /// <summary>
    /// A refit rewrites the host through the same writer, so it has to come out the same way. This is the path the
    /// report came from — a garment refitted onto another body, opened in Penumbra's model editor.
    /// </summary>
    [Fact]
    public void A_rebuilt_host_carries_its_names_too()
    {
        var sliced = SliceByCount(Rebuilt());
        Assert.DoesNotContain("", sliced);
        Assert.Contains(Material, sliced);
        Assert.Contains("n_hte_l", sliced);
        Assert.Contains("atr_ude", sliced);
    }
}
