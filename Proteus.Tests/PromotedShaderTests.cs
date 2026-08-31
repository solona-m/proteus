using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="RenderModeInference.PromotedShader"/> — which shader an auto-promoted overlay renders on.
/// <para/>
/// It sits beside <see cref="RenderModeInference.ShouldPromoteToGear"/> for the same reason that predicate
/// does: the compositor and the editor both ask it, so they cannot disagree about what was composited. The
/// editor previously hardcoded character.shpk here and so offered sphere/metal/glow controls for an overlay
/// actually rendering on skin.shpk, where the colour table cannot be addressed at all.
/// </summary>
public class PromotedShaderTests
{
    private static ColorTableRowPreset Row(int row) => new() { Row = row };

    /// <summary>
    /// The whole point: art authored as skin keeps skin shading, and with it the wearer's tone — which
    /// reaches art through the normal's blue channel that a gear shell spends on its alpha gate.
    /// </summary>
    [Fact]
    public void A_plain_promoted_overlay_stays_on_the_skin_shader()
    {
        Assert.Equal(OverlayDescriptor.SkinShader,
            RenderModeInference.PromotedShader(new OverlayDescriptor(), null));
        Assert.Equal(OverlayDescriptor.SkinShader,
            RenderModeInference.PromotedShader(new OverlayDescriptor(), []));
    }

    /// <summary>
    /// Any colour row at all forces character.shpk. skin.shpk declares no g_SamplerIndex, so nothing can
    /// select a row per texel — the table would be inert and the author's colours would silently vanish.
    /// </summary>
    [Fact]
    public void Colour_rows_force_the_gear_shader()
    {
        Assert.Equal(OverlayDescriptor.DefaultGearShader,
            RenderModeInference.PromotedShader(new OverlayDescriptor(), [Row(16)]));
    }

    /// <summary>A scroll effect needs characterscroll's animated emissive, which skin.shpk has no part of.</summary>
    [Fact]
    public void A_scroll_effect_forces_the_gear_shader()
    {
        Assert.Equal(OverlayDescriptor.DefaultGearShader,
            RenderModeInference.PromotedShader(new OverlayDescriptor { Scroll = "glow.dds" }, null));
    }

    /// <summary>A mask shell is coloured entirely by its colorset over a white base — exactly the thing
    /// skin.shpk cannot address.</summary>
    [Fact]
    public void A_mask_shell_forces_the_gear_shader()
    {
        Assert.Equal(OverlayDescriptor.DefaultGearShader,
            RenderModeInference.PromotedShader(new OverlayDescriptor { IsMaskShell = true }, null));
    }

    /// <summary>An author who named a shader keeps it — promotion never overrides a stated choice.</summary>
    [Fact]
    public void An_authored_shader_is_preserved()
    {
        Assert.Equal("characterscroll.shpk",
            RenderModeInference.PromotedShader(
                new OverlayDescriptor { Shader = "characterscroll.shpk" }, null));
        Assert.Equal(OverlayDescriptor.DefaultGearShader,
            RenderModeInference.PromotedShader(
                new OverlayDescriptor { Shader = OverlayDescriptor.DefaultGearShader }, null));
    }
}
