using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Whether a slot whose new body draws smallclothes in place of skin can bring them in (see
/// <see cref="BodyRetarget.ReadySmallclothes"/>), on synthetic bodies (see <see cref="SkinlessTargetTests"/>).
/// </summary>
public class SkinlessTargetDownstreamTests
{
    private static readonly byte[] BoxersMaterial = [4, 2];

    /// <summary>A worn pair: the sheet onto the same sheet with its hips cut out and boxers laid over them.</summary>
    private static BodyRetarget.SlotPair WornPair(System.Func<string, byte[]?>? material)
    {
        var (source, sourceUv) = SkinlessTargetTests.Sheet(SkinlessTargetTests.Skin);
        var (target, targetUv) = SkinlessTargetTests.Target((0.6f, 0.9f), SkinlessTargetTests.Boxers, 0.003f);
        Assert.True(UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, "legs", out var built,
                                                   out string refusal, out _, sameLayout: true), refusal);
        return new BodyRetarget.SlotPair("_dwn", built!, target, TargetModel: [0], SourceModel: [0],
                                         TargetMaterial: material);
    }

    /// <summary>A garment drawing <paramref name="materials"/>, one mesh each.</summary>
    private static byte[] Garment(params string[] materials)
        => SyntheticModel.Build([], materials.Select(m => new SyntheticModel.Mesh(m, new SyntheticModel.Sub(0))).ToArray());

    private static byte[]? Muse(string leaf) => leaf == "mt_c0201e0000_dwn_b.mtrl" ? BoxersMaterial : null;

    [Fact]
    [Trait("Plan", "AC-skinless-target-8")]
    public void A_worn_slot_brings_its_boxers_in_under_the_garment_s_own_name()
    {
        var pairs = BodyRetarget.ReadySmallclothes(Garment("/mt_c0201e6144_dwn_a.mtrl"), [WornPair(Muse)], replaceSkin: true,
                                                   out var materials, out int fallback);
        Assert.Equal(0, fallback);
        var pair = Assert.Single(pairs);
        Assert.False(pair.ShapeOnly);
        Assert.Equal("/mt_c0201e6144_dwn_z.mtrl", pair.Smallclothes![SkinlessTargetTests.Boxers]);
        var material = Assert.Single(materials);
        Assert.Equal("mt_c0201e6144_dwn_z.mtrl", material.Name);
        Assert.Equal(BoxersMaterial, material.Bytes);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-22")]
    public void Without_the_underwear_material_the_slot_keeps_the_garment_s_own_skin()
    {
        var pairs = BodyRetarget.ReadySmallclothes(Garment("/mt_c0201e6144_dwn_a.mtrl"), [WornPair(_ => null)],
                                                   replaceSkin: true, out var materials, out int fallback);
        Assert.Equal(1, fallback);
        Assert.True(pairs[0].ShapeOnly);
        Assert.False(pairs[0].Swaps);
        Assert.Empty(materials);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-22")]
    public void A_body_mod_that_cannot_be_read_keeps_the_garment_s_own_skin()
    {
        var pairs = BodyRetarget.ReadySmallclothes(Garment("/mt_c0201e6144_dwn_a.mtrl"), [WornPair(null)],
                                                   replaceSkin: true, out _, out int fallback);
        Assert.Equal(1, fallback);
        Assert.True(pairs[0].ShapeOnly);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-23")]
    public void Without_material_room_the_slot_keeps_the_garment_s_own_skin()
    {
        // Nine of the ten materials, and the skin's layer takes the last.
        var full = Enumerable.Range(0, 9).Select(i => $"/mt_c0201e6144_dwn_{(char)('a' + i)}.mtrl").ToArray();
        var pairs = BodyRetarget.ReadySmallclothes(Garment(full), [WornPair(Muse)], replaceSkin: true, out _, out int fallback);
        Assert.Equal(1, fallback);
        Assert.True(pairs[0].ShapeOnly);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-32")]
    public void One_free_material_slot_goes_to_the_boxers()
    {
        // Eight materials, the skin's layer: one slot left, which the boxers take before any nails could.
        var eight = Enumerable.Range(0, 8).Select(i => $"/mt_c0201e6144_dwn_{(char)('a' + i)}.mtrl").ToArray();
        var pairs = BodyRetarget.ReadySmallclothes(Garment(eight), [WornPair(Muse)], replaceSkin: true, out var materials,
                                                   out int fallback);
        Assert.Equal(0, fallback);
        Assert.NotNull(pairs[0].Smallclothes);
        Assert.Single(materials);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-24")]
    public void A_garment_with_no_material_of_its_own_keeps_its_own_skin()
    {
        var pairs = BodyRetarget.ReadySmallclothes(Garment(SkinlessTargetTests.Skin), [WornPair(Muse)], replaceSkin: true,
                                                   out _, out int fallback);
        Assert.Equal(1, fallback);
        Assert.True(pairs[0].ShapeOnly);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-29")]
    public void Without_the_skin_swap_nothing_is_readied_and_the_pair_stays_worn()
    {
        var pair = WornPair(Muse);
        var pairs = BodyRetarget.ReadySmallclothes(Garment("/mt_c0201e6144_dwn_a.mtrl"), [pair], replaceSkin: false,
                                                   out var materials, out int fallback);
        Assert.Same(pairs[0].Correspondence, pair.Correspondence);
        Assert.Null(pairs[0].Smallclothes);
        Assert.Empty(materials);
        Assert.Equal(0, fallback);
        // The rest of the refit still reads the body as worn: the surface and edges come from the smallclothes too.
        Assert.True(pairs[0].Worn);
        Assert.Contains(pairs[0].TargetSurface().SkinVertices, v => pair.Target.Positions[v * 3 + 2] > 0.002f);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-7")]
    public void A_pair_that_is_not_worn_reads_skin_alone_as_always()
    {
        var (source, sourceUv) = SkinlessTargetTests.Sheet(SkinlessTargetTests.Skin);
        var (target, targetUv) = SkinlessTargetTests.Target((0.85f, 0.9f), SkinlessTargetTests.Boxers, 0.003f);
        Assert.True(UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, "legs", out var built,
                                                   out string refusal, out _, sameLayout: true), refusal);
        var pair = new BodyRetarget.SlotPair("_dwn", built!, target, TargetModel: [0], SourceModel: [0]);
        Assert.False(pair.Worn);
        Assert.DoesNotContain(pair.TargetSurface().SkinVertices, v => target.Positions[v * 3 + 2] > 0.002f);
        var pairs = BodyRetarget.ReadySmallclothes(Garment("/mt_c0201e6144_dwn_a.mtrl"), [pair], replaceSkin: true,
                                                   out _, out _);
        Assert.Equal(pair, pairs[0]);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-33")]
    public void Materials_survive_a_plan_s_model_being_replaced()
    {
        var plan = new BodyRetarget.Planned(new RetargetEdit([], [], []), [1],
                                            new BodyRetarget.Report(0, 0, 0, 0, 0, 0f, 0f, 0, false),
                                            [new BodyRetarget.PublishedMaterial("mt_c0201e6144_dwn_z.mtrl", BoxersMaterial, "/b")]);
        var finished = plan with { Model = [2] };   // as RefitCore.Finish, WithBodySkin and the racial bake do
        Assert.Same(plan.Materials, finished.Materials);
    }
}
