using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// cross-sex-wrist-band: a women's glove made a men's lets go of the hands whose skin is bare (their skin is swapped for
/// the men's own) and keeps holding the others exactly as made.
/// </summary>
public class KeepEndsReshapeTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e0613_glv_a.mtrl";

    /// <summary>A two-handed glove: four points per hand (x = ±0.5), one triangle of skin on <paramref name="skinLeft"/> /
    /// <paramref name="skinRight"/> hands and one of cloth on each.</summary>
    private static ModelParts Glove(bool skinLeft, bool skinRight, uint skinMask = 0, IReadOnlyList<string>? names = null)
    {
        var pos = new List<float>();
        void Add(float x, float y, float z) => pos.AddRange([x, y, z]);
        foreach (float side in new[] { 0.5f, -0.5f })
        {
            Add(side, 1f, 0f); Add(side + 0.01f, 1f, 0f); Add(side, 1.01f, 0f);   // skin triangle
            Add(side, 1f, 0.01f); Add(side + 0.01f, 1f, 0.01f); Add(side, 1.01f, 0.01f); // cloth triangle
        }
        var parts = new List<ModelPart>();
        int sub = 0;
        ModelPart Part(int mesh, string material, int[] tris, uint mask = 0) => new()
        {
            Mesh = mesh, Submesh = sub++, Island = -1, Label = $"{mesh + 1}.{sub}", Material = material, Triangles = tris,
            Ordinals = Enumerable.Range(0, tris.Length / 3).ToArray(), AttributeMask = mask,
            Min = Vector3.Zero, Max = Vector3.One, Toggleable = true,
        };
        if (skinLeft) parts.Add(Part(0, Skin, [0, 1, 2], skinMask));
        if (skinRight) parts.Add(Part(0, Skin, [6, 7, 8], skinMask));
        parts.Add(Part(1, Cloth, [3, 4, 5]));
        parts.Add(Part(1, Cloth, [9, 10, 11]));
        return new ModelParts
        {
            Positions = pos.ToArray(), Normals = new float[pos.Count], MeshSpans = [new MeshSpan(0, 0, 12)], Parts = parts,
            AttributeNames = names ?? [], Min = new Vector3(-1), Max = new Vector3(1),
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    private static KeepEnds.Reading AllHeld(ModelParts g)
        => new("_glv", "_top", Enumerable.Range(0, 12).ToHashSet(), g, [], 0, 12);

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-5")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-4")]
    public void Only_skinned_sides_are_released() // [EDGE-cross-sex-wrist-band-5] [EDGE-cross-sex-wrist-band-4]
    {
        var left = Glove(skinLeft: true, skinRight: false);
        var one = KeepEnds.Reshape(AllHeld(left), left, null, out _)!;
        Assert.NotNull(one);
        // Only the right (armoured) hand stays held: its six points, all at x < 0.
        Assert.Equal(6, one.Ends.Count);
        Assert.All(one.Ends, v => Assert.True(left.Positions[3 * v] < 0f));

        var both = Glove(skinLeft: true, skinRight: true);
        var two = KeepEnds.Reshape(AllHeld(both), both, null, out _)!;
        Assert.Empty(two.Ends);
    }

    /// <summary>A cuff on the let-go hand moves whole with it; one on the hand still held stays held.</summary>
    [Fact]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-7")]
    public void Free_pieces_recomputed_for_the_new_hold() // [EDGE-cross-sex-wrist-band-7]
    {
        var left = Glove(skinLeft: true, skinRight: false);
        IReadOnlyCollection<int> leftCuff = [3, 4, 5], rightCuff = [9, 10, 11];
        var reshaped = KeepEnds.Reshape(AllHeld(left), left, null, out _, [leftCuff, rightCuff], ["left cuff", "right cuff"])!;
        Assert.Contains(reshaped.FreePieces, f => f.Count == 3 && f.All(leftCuff.Contains));
        Assert.DoesNotContain(reshaped.FreePieces, f => f.Any(rightCuff.Contains));
        Assert.Equal(["left cuff"], reshaped.FreeNames);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-1")]
    public void Skinless_glove_does_not_reshape() // [EDGE-cross-sex-wrist-band-1]
    {
        var none = Glove(skinLeft: false, skinRight: false);
        Assert.Null(KeepEnds.Reshape(AllHeld(none), none, null, out string why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-6")]
    public void User_held_skin_blocks_reshape() // [EDGE-cross-sex-wrist-band-6]
    {
        var left = Glove(skinLeft: true, skinRight: false);
        Assert.Null(KeepEnds.Reshape(AllHeld(left), left, new HashSet<int> { 1 }, out string why));
        Assert.False(string.IsNullOrEmpty(why));
        // Held cloth of the armoured hand does not block it.
        Assert.NotNull(KeepEnds.Reshape(AllHeld(left), left, new HashSet<int> { 9 }, out _));
    }

    /// <summary>The writer's input for a swapped-in skin: every tag on the new body's skin the glove's own skin did not
    /// carry is taken off, alongside the body mod's switched tags; with no glove tags given, only the switched ones, as
    /// always.</summary>
    [Fact]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-8")]
    public void Swapped_skin_drops_the_tags_the_glove_did_not_carry() // [EDGE-cross-sex-wrist-band-8]
    {
        var body = Glove(skinLeft: true, skinRight: true, skinMask: 0b11, names: ["atr_ude", "atr_kos"]);
        var pair = default(BodyRetarget.SlotPair) with { Slot = "_glv", Target = body, TargetSwitched = new HashSet<string> { "atrx_switch" } };
        Assert.Equal(["atrx_switch"], BodyRetarget.SkinTagsDropped(pair, null)!.Order());
        Assert.Equal(["atr_kos", "atr_ude", "atrx_switch"], BodyRetarget.SkinTagsDropped(pair, new HashSet<string>())!.Order());
        Assert.Equal(["atr_kos", "atrx_switch"], BodyRetarget.SkinTagsDropped(pair, new HashSet<string> { "atr_ude" })!.Order());
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-8")]
    public void Skin_tags_are_the_gloves_own() // [EDGE-cross-sex-wrist-band-8]
    {
        var tagged = Glove(skinLeft: true, skinRight: false, skinMask: 0b10, names: ["atr_tv_a", "atr_ude"]);
        Assert.Equal(["atr_ude"], KeepEnds.SkinTagsOf(tagged).Order());
        Assert.Empty(KeepEnds.SkinTagsOf(Glove(skinLeft: true, skinRight: false)));
    }
}
