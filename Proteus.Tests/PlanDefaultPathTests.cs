using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Every refit but the automatic one of shoes and gloves — chest and legs pieces, the Studio's Body size tool, the
/// headless refit — goes through <see cref="BodyRetarget.Plan"/> without a swap slot, and must come out byte for byte as
/// it did before shoes and gloves learned to keep their ends. The hashes were taken from <c>test</c> before the change.
/// </summary>
public class PlanDefaultPathTests(ITestOutputHelper output)
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e6255_top_a.mtrl";

    /// <summary>A slot whose body moves out by <paramref name="grow"/> along Z, its new body's file carried for the swap.</summary>
    private static BodyRetarget.SlotPair Grown(string slot, Func<float, byte[]> body, float grow)
    {
        var source = ModelPartReader.Read(body(0f))!;
        var targetBytes = body(grow);
        var target = ModelPartReader.Read(targetBytes)!;
        Assert.True(IdentityCorrespondence.TryBuild(source, target, slot, out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, target, targetBytes, body(0f));
    }

    /// <summary>Chest skin (mesh 0) lying on the chest, legs skin (mesh 1) on the legs, and cloth over the chest.</summary>
    private static byte[] Garment() => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)),
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 3f)),
        new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));

    private static byte[] Chest(float z) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 5, OffsetZ: z)));

    private static byte[] Legs(float z) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetX: 3f, OffsetZ: z)));

    private static string Hash(BodyRetarget.Planned plan) => Convert.ToHexString(SHA256.HashData(plan.Model))[..16];

    public static TheoryData<string, string> Pinned => new()
    {
        { "own slot swapped", "D822FCBCE979C1D0" },
        { "own slot swapped, part held", "D822FCBCE979C1D0" },
        { "a reached slot", "C7165EA5D9B4DDEE" },
        { "no swap", "08808348C0E19134" },
    };

    [Theory]
    [MemberData(nameof(Pinned))]
    [Trait("Plan", "AC-keep-hand-foot-skin-10")]
    public void Plan_without_a_swap_slot_is_unchanged(string fixture, string pinned)
    {
        var bytes = Garment();
        var garment = ModelPartReader.Read(bytes)!;
        var chest = Grown("_top", Chest, 0.01f);
        var legs = Grown("_dwn", Legs, 0.02f);
        var plan = fixture switch
        {
            "own slot swapped" => BodyRetarget.Plan(garment, bytes, [chest], "_top", replaceSkin: true, cutHidden: false),
            "own slot swapped, part held" => BodyRetarget.Plan(garment, bytes, [chest], "_top",
                                                               held: new HashSet<int> { 6, 7, 8 }, replaceSkin: true,
                                                               cutHidden: false),
            "a reached slot" => BodyRetarget.Plan(garment, bytes, [chest, legs], "_top", replaceSkin: true,
                                                  acrossBodies: true, cutHidden: true),
            _ => BodyRetarget.Plan(garment, bytes, [chest], "_top"),
        };
        output.WriteLine($"{fixture}: {Hash(plan)} (swap {plan.Report.Swap}, moved up to {plan.Report.WorstMove})");
        Assert.Equal(pinned, Hash(plan));
    }
}
