using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A reinforced-toe normal is published content-addressed so the game cannot keep serving a cached copy.
/// Everything that recognises a shell normal, or derives its index or material from it, reads the name
/// through <see cref="ShellTextureNames"/> — and each used to strip a literal "_norm.tex", which a hashed
/// name fails silently. These pin both forms.
/// </summary>
public class ShellTextureNamesTests
{
    private const string Hex = "0123456789abcdef";

    [Theory]
    [InlineData("ss_2_norm.tex", "ss_2")]
    [InlineData("ss_2_norm_" + Hex + ".tex", "ss_2")]
    [InlineData("SS_K_NORM_0123456789ABCDEF.TEX", "SS_K")]
    public void TryNormalStem_AcceptsBothForms(string leaf, string expected)
    {
        Assert.True(ShellTextureNames.TryNormalStem(leaf, out var stem));
        Assert.Equal(expected, stem);
    }

    [Theory]
    [InlineData("ss_2_id.tex")]
    [InlineData("ss_2_mask.tex")]
    [InlineData("ss_2_norm_0123.tex")]                 // too short to be the content hash
    [InlineData("ss_2_norm_0123456789abcdeg.tex")]     // not hex
    [InlineData("eye01_norm.tex")]                     // not a shell
    [InlineData("ss__norm.tex")]                       // no letter
    [InlineData("")]
    public void TryNormalStem_RejectsEverythingElse(string leaf)
        => Assert.False(ShellTextureNames.TryNormalStem(leaf, out _));

    /// <summary>The index sits beside the normal under its FIXED name, whichever form the normal took.</summary>
    [Theory]
    [InlineData(@"E:\Penumbradt\Proteus\textures\ss_2_norm.tex", @"E:\Penumbradt\Proteus\textures\ss_2_id.tex")]
    [InlineData(@"E:\Penumbradt\Proteus\textures\ss_2_norm_" + Hex + ".tex", @"E:\Penumbradt\Proteus\textures\ss_2_id.tex")]
    [InlineData("E:/Penumbradt/Proteus/textures/ss_2_norm_" + Hex + ".tex", "E:/Penumbradt/Proteus/textures/ss_2_id.tex")]
    public void IndexBeside_FindsTheFixedIndex(string normal, string expected)
        => Assert.Equal(expected, ShellTextureNames.IndexBeside(normal));

    [Theory]
    [InlineData("ss_2_norm.tex")]
    [InlineData("ss_2_norm_" + Hex + ".tex")]
    public void MaterialLeaf_IsTheShellsMaterial(string leaf)
        => Assert.Equal("ss_2.mtrl", ShellTextureNames.MaterialLeaf(leaf));

    /// <summary>
    /// Every revision of one shell's normal must share a cache key, or the services that decode it would add a
    /// full-resolution buffer per revision and never replace one.
    /// </summary>
    [Fact]
    public void ShellKey_IsTheSameForEveryRevisionOfOneShell()
    {
        var a = ShellTextureNames.ShellKey(@"E:\P\textures\ss_2_norm.tex");
        var b = ShellTextureNames.ShellKey(@"E:\P\textures\ss_2_norm_" + Hex + ".tex");
        var c = ShellTextureNames.ShellKey(@"""E:\P\textures\ss_2_norm_fedcba9876543210.tex"" ");
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.NotEqual(a, ShellTextureNames.ShellKey(@"E:\P\textures\ss_3_norm.tex"));
    }

    [Fact]
    public void ContentAddressedNormal_RoundTripsThroughTheParser()
    {
        var name = ShellTextureNames.ContentAddressedNormal('k', 0xDEADBEEF12345678ul);
        Assert.Equal("ss_k_norm_deadbeef12345678.tex", name);
        Assert.True(ShellTextureNames.TryNormalStem(name, out var stem));
        Assert.Equal("ss_k", stem);
    }
}
