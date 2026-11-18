using System;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The featureless bulge through <see cref="BodyRetarget.Plan"/>: a garment covering the crotch of a body with a penis is
/// fitted over a smooth bulge instead, and every other refit comes out exactly as it did before the bulge existed.
/// </summary>
public partial class BulgeRefitTests
{
    /// <summary>
    /// The refits the bulge must never touch, hashed as they came out before it was built: a women's pair, a men's pair
    /// whose pouch is its own material, and the same garment kept and swapped. Captured on the code before the bulge
    /// (2026-11-18, build 1482) and never re-recorded: a change here is the bulge reaching a refit it must leave alone.
    /// </summary>
    [Theory]
    [Trait("Plan", "AC-featureless-bulge-6")]
    [InlineData("women", false, false, "601EA4F93F43CE3F1531E99FC4D20EAD1280265C3A706D89906BEAB4F3FA94C6")]
    [InlineData("women", true, true, "3A97E9F56139568CACBB50919DF25E384DE9AE54A3379934F69C60B5C78CB122")]
    [InlineData("pouch", false, false, "601EA4F93F43CE3F1531E99FC4D20EAD1280265C3A706D89906BEAB4F3FA94C6")]
    [InlineData("pouch", true, true, "46DDF6298EF7137B85D00E8BD79BA00741BA01FA1C58872D57BBFA28A0AC4678")]
    public void Plan_without_a_bulge_is_bit_identical(string bodies, bool replaceSkin, bool acrossBodies, string expected)
    {
        bool pouch = bodies == "pouch";
        var source = SyntheticCrotch.Body(pouch: pouch);
        var target = SyntheticCrotch.Body(pouch: pouch, scale: 1.05f);
        var garment = SyntheticCrotch.Garment(SyntheticCrotch.Briefs, stitch: _ => true,
                                              skin: (part, p) => part == SyntheticCrotch.Part.Hips && p.Y > SyntheticCrotch.Crotch + 0.07f);
        var parts = ModelPartReader.Read(garment)!;
        var pairs = new[] { SyntheticCrotch.Pair(source, target) };

        var planned = BodyRetarget.Plan(parts, (byte[])garment.Clone(), pairs, garmentSlot: "_dwn",
                                        replaceSkin: replaceSkin, acrossBodies: acrossBodies);

        string actual = Convert.ToHexString(SHA256.HashData(planned.Model));
        Assert.True(expected == actual, $"hash {bodies}/{replaceSkin}/{acrossBodies} = {actual}");
    }
}
