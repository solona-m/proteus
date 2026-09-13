using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which texture paths the skin compositor must never redirect, and the private path it publishes at instead.
/// <para/>
/// A vanilla skin material names <c>chara/common/texture/skin_mask.tex</c> for its mask. Redirecting that
/// applies to every character in the collection, and Penumbra refuses it outright as a "Reserved File
/// Redirection" — so the composited mask was dropped, and the warning was the only sign.
/// </summary>
public class SharedTexturePathTests
{
    /// <summary>Penumbra's whole reserved list (ReservedFiles.Files), plus the shared eye maps it serves but
    /// which leak the same way.</summary>
    [Theory]
    [InlineData("chara/common/texture/skin_mask.tex")]
    [InlineData("chara/common/texture/white.tex")]
    [InlineData("chara/common/texture/black.tex")]
    [InlineData("chara/common/texture/id_16.tex")]
    [InlineData("chara/common/texture/common_id.tex")]
    [InlineData("chara/common/texture/red.tex")]
    [InlineData("chara/common/texture/green.tex")]
    [InlineData("chara/common/texture/blue.tex")]
    [InlineData("chara/common/texture/null_normal.tex")]
    [InlineData("common/graphics/texture/dummy.tex")]
    [InlineData("chara/common/texture/eye/eye01_base.tex")]
    [InlineData("CHARA/Common/Texture/skin_mask.tex")]
    public void SharedTexturesAreRecognised(string path)
        => Assert.True(CompositorService.IsSharedTexturePath(path));

    [Theory]
    [InlineData("chara/human/c0201/obj/body/b0001/texture/c0201b0001_base.tex")]
    [InlineData("chara/human/c0201/obj/face/f0001/texture/c0201f0001_fac_mask.tex")]
    [InlineData("chara/bibo_mid_mask.tex")]
    [InlineData("chara/proteus/mt_c0201b0001_a_0123abcd_m.tex")]
    public void PerCharacterTexturesAreNot(string path)
        => Assert.False(CompositorService.IsSharedTexturePath(path));

    /// <summary>
    /// The private path is written INTO a material copy, so it must not move between composites of the same
    /// material — and it must differ between materials that share a file stem in different folders.
    /// </summary>
    [Fact]
    public void OwnedPathIsStablePerMaterialAndSlot()
    {
        const string a = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
        const string b = "chara/human/c0201/obj/body/b0001/material/v0002/mt_c0201b0001_a.mtrl";

        var owned = CompositorService.OwnedTexturePath(a, "m");
        Assert.Equal(owned, CompositorService.OwnedTexturePath(a, "m"));
        Assert.Equal(owned, CompositorService.OwnedTexturePath(a.ToUpperInvariant(), "m"));
        Assert.NotEqual(owned, CompositorService.OwnedTexturePath(a, "n"));
        Assert.NotEqual(owned, CompositorService.OwnedTexturePath(b, "m"));

        Assert.StartsWith(CompositorService.OwnedTextureRoot, owned);
        Assert.StartsWith(CompositorService.OwnedTextureRoot + "mt_c0201b0001_a_", owned);
        Assert.EndsWith("_m.tex", owned);
        Assert.False(CompositorService.IsSharedTexturePath(owned));
    }
}
