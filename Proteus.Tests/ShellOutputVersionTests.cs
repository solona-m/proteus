using System;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The merged shell must declare the .mdl version it actually WRITES, not the one its first source happened
/// to have.
/// <para/>
/// The writer only ever emits v6 bone tables (a header array plus a shared index pool), but it copied the
/// 0x44 file header — version included — verbatim from source 0. When that source was a v5 model the output
/// claimed v5 while holding v6 tables, so the game parsed them as v5's fixed 132-byte structs, every mesh's
/// table resolved to unrelated bytes, and each vertex weighted to whatever joint those bytes named. The
/// shell flailed across the character.
/// <para/>
/// It stayed hidden while every source was v6. Gear that ships its own vanilla skin is what breaks that
/// assumption — those models are frequently v5, and the slot they come from can sort first.
/// </summary>
public class ShellOutputVersionTests
{
    private const string BodyMaterial = "/mt_c0201b0001_a.mtrl";

    private static byte[] Source(uint version) => SyntheticModel.Build(
        ["atr_top"],
        [new SyntheticModel.Mesh(BodyMaterial, new SyntheticModel.Sub(0))],
        version);

    private static byte[] BuildShell(params byte[][] sources)
    {
        var specs = sources.Select(m => new SecondSkinWriter.SourceSpec(m)).ToList();
        var layer = new SecondSkinLayer { MaterialName = "/ss_0.mtrl" };
        return SecondSkinWriter.Build(specs, [layer], null, out _, null);
    }

    /// <summary>
    /// The case that broke: a v5 source at index 0. The output must still say v6, because that is the bone
    /// table format it contains.
    /// </summary>
    [Theory]
    [InlineData(SyntheticModel.V5)]
    [InlineData(SyntheticModel.V6)]
    public void The_shell_declares_v6_whatever_the_first_source_was(uint sourceVersion)
    {
        var shell = BuildShell(Source(sourceVersion));
        Assert.Equal(SyntheticModel.V6, BitConverter.ToUInt32(shell, 0));
    }

    /// <summary>
    /// Mixed sources — the real wardrobe, a vanilla-skin garment beside body-mod parts — and the order must
    /// not matter. Whichever lands at index 0, the file describes itself correctly.
    /// </summary>
    [Fact]
    public void Mixing_versions_still_declares_v6_in_either_order()
    {
        Assert.Equal(SyntheticModel.V6,
            BitConverter.ToUInt32(BuildShell(Source(SyntheticModel.V5), Source(SyntheticModel.V6)), 0));
        Assert.Equal(SyntheticModel.V6,
            BitConverter.ToUInt32(BuildShell(Source(SyntheticModel.V6), Source(SyntheticModel.V5)), 0));
    }

    /// <summary>
    /// And the file has to survive a round trip through the reader that declares that version — the bone
    /// tables being readable as v6 is the whole point of stamping v6.
    /// </summary>
    [Theory]
    [InlineData(SyntheticModel.V5)]
    [InlineData(SyntheticModel.V6)]
    public void The_shells_bone_tables_read_back_as_written(uint sourceVersion)
    {
        var parsed = SecondSkinWriter.Parse(BuildShell(Source(sourceVersion)));

        Assert.NotEmpty(parsed.BoneNames);
        var table = Assert.Single(parsed.BoneTables);
        Assert.NotEmpty(table);
        // Every entry must name a real bone in the merged model's union list — the check that fails outright
        // when a v6 table is read through the v5 layout.
        Assert.All(table, b => Assert.True(b < parsed.BoneNames.Length,
            $"bone table entry {b} is past the {parsed.BoneNames.Length}-bone union list"));
    }
}
