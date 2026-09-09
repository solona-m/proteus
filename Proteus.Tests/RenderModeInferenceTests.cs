using System.Collections.Generic;
using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for <see cref="RenderModeInference"/> — the pure logic that derives an overlay's render mode
/// (Skin / Cloth / Glow) from the features actually in use. No ImGui or game data.
/// </summary>
public class RenderModeInferenceTests
{
    private static List<ColorTableRowPreset> Rows(ColorTableSubRowPreset? a = null, ColorTableSubRowPreset? b = null)
        => new() { new ColorTableRowPreset { Row = 16, SubRowA = a, SubRowB = b } };

    private static OverlayDescriptor Desc(string? scroll = null)
        => new() { Scroll = scroll };

    // ── IsClothSub ────────────────────────────────────────────────────────────

    [Fact]
    public void PlainSubRow_IsNotCloth()
        => Assert.False(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Diffuse = "#FFF" }));

    [Fact]
    public void NullSubRow_IsNotCloth() => Assert.False(RenderModeInference.IsClothSub(null));

    // Skin can no longer emit — the normal-alpha bake and the skin.shpk glow key are gone — so glow is
    // a Cloth feature like any other the skin shader can't do. This assertion used to read False.
    [Fact]
    public void Glow_IsCloth()
        => Assert.True(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Diffuse = "#FFF", Emissive = 0.5f }));

    [Fact]
    public void ZeroGlow_IsNotCloth()
        => Assert.False(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Emissive = 0f }));

    [Theory]
    [MemberData(nameof(ClothSubRows))]
    public void ClothFeature_IsCloth(ColorTableSubRowPreset sub)
        => Assert.True(RenderModeInference.IsClothSub(sub));

    public static IEnumerable<object[]> ClothSubRows() => new[]
    {
        new object[] { new ColorTableSubRowPreset { Specular = "#808080" } },
        new object[] { new ColorTableSubRowPreset { Metalness = 0.3f } },
        new object[] { new ColorTableSubRowPreset { SphereMap = 4 } },
        new object[] { new ColorTableSubRowPreset { SphereIntensity = 0.7f } },
        new object[] { new ColorTableSubRowPreset { Tile = 12 } },
    };

    /// <summary>
    /// Tile SLICE ZERO is a real weave, so it counts — unlike sphere 0, which is the game's own empty entry.
    /// <para/>
    /// The trap is that the sphere beside it reads <c>SphereMap.GetValueOrDefault() &gt; 0</c>, and copying
    /// that idiom here makes the first of the sixty-four weaves the one slice that cannot be used: the row
    /// would never promote, the overlay would stay on skin.shpk, and picking it would do nothing at all.
    /// </summary>
    [Fact]
    public void TileSliceZero_IsCloth()
        => Assert.True(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Tile = 0 }));

    /// <summary>A scale with no pattern to apply it to renders nothing, so it is not a reason to promote.</summary>
    [Fact]
    public void TileScaleAlone_IsNotCloth()
        => Assert.False(RenderModeInference.IsClothSub(
            new ColorTableSubRowPreset { TileScaleU = 8f, TileScaleV = 8f, TileStrength = 1f }));

    [Fact]
    public void ExplicitZeroMetal_IsNotCloth()   // metal 0 / sphere 0 are "off", not a Cloth signal
        => Assert.False(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Metalness = 0f, SphereMap = 0, SphereIntensity = 0f }));

    [Fact]
    public void RoughnessAlone_IsNotCloth()   // roughness does nothing without metal/sphere; skin has it too
        => Assert.False(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Roughness = 0f }));

    // ── Infer: single-signal cases ─────────────────────────────────────────────

    [Fact]
    public void NoFeatures_IsSkin()
        => Assert.Equal(RenderMode.Skin,
            RenderModeInference.Infer(Rows(), new[] { Desc() }, null, RenderMode.Cloth, FeatureEdit.Neutral));

    [Fact]
    public void SphereMap_ImpliesCloth()
        => Assert.Equal(RenderMode.Cloth,
            RenderModeInference.Infer(Rows(new ColorTableSubRowPreset { SphereMap = 3 }),
                new[] { Desc() }, null, RenderMode.Skin, FeatureEdit.Cloth));

    [Fact]
    public void GlowOnSkinRow_ImpliesCloth()
        => Assert.Equal(RenderMode.Cloth,
            RenderModeInference.Infer(Rows(new ColorTableSubRowPreset { Emissive = 0.5f }),
                new[] { Desc() }, null, RenderMode.Skin, FeatureEdit.Cloth));

    [Fact]
    public void ZeroGlowOnSkinRow_StaysSkin()
        => Assert.Equal(RenderMode.Skin,
            RenderModeInference.Infer(Rows(new ColorTableSubRowPreset { Emissive = 0f }),
                new[] { Desc() }, null, RenderMode.Skin, FeatureEdit.Cloth));

    [Fact]
    public void ScrollEffect_ImpliesGlow()
        => Assert.Equal(RenderMode.Glow,
            RenderModeInference.Infer(Rows(), new[] { Desc(scroll: "neon.tex") }, null, RenderMode.Skin, FeatureEdit.Glow));

    // ── Infer: conflict (both Cloth and Glow set) — last edit wins ──────────────

    private static (List<ColorTableRowPreset>, OverlayDescriptor[]) Conflict()
        => (Rows(new ColorTableSubRowPreset { SphereMap = 2 }), new[] { Desc(scroll: "neon.tex") });

    [Fact]
    public void Conflict_LastEditCloth_WinsCloth()
    {
        var (rows, ov) = Conflict();
        Assert.Equal(RenderMode.Cloth,
            RenderModeInference.Infer(rows, ov, null, RenderMode.Glow, FeatureEdit.Cloth));
    }

    [Fact]
    public void Conflict_LastEditGlow_WinsGlow()
    {
        var (rows, ov) = Conflict();
        Assert.Equal(RenderMode.Glow,
            RenderModeInference.Infer(rows, ov, null, RenderMode.Cloth, FeatureEdit.Glow));
    }

    [Fact]
    public void Conflict_NoHint_KeepsCurrentWinner()
    {
        var (rows, ov) = Conflict();
        Assert.Equal(RenderMode.Cloth,
            RenderModeInference.Infer(rows, ov, null, RenderMode.Cloth, FeatureEdit.Neutral));
        Assert.Equal(RenderMode.Glow,
            RenderModeInference.Infer(rows, ov, null, RenderMode.Glow, FeatureEdit.Neutral));
    }

    // ── Infer: downgrade when the last Cloth feature is cleared ─────────────────

    [Fact]
    public void ClearingLastCloth_WithScroll_FallsToGlow()
        => Assert.Equal(RenderMode.Glow,
            RenderModeInference.Infer(Rows(), new[] { Desc(scroll: "neon.tex") }, null, RenderMode.Cloth, FeatureEdit.Cloth));

    [Fact]
    public void ClearingLastCloth_NoScroll_FallsToSkin()
        => Assert.Equal(RenderMode.Skin,
            RenderModeInference.Infer(Rows(), new[] { Desc() }, null, RenderMode.Cloth, FeatureEdit.Cloth));

    // ── ModeOf (badge) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OverlayLayer.Skin, null, RenderMode.Skin)]
    [InlineData(OverlayLayer.Gear, "character.shpk", RenderMode.Cloth)]
    [InlineData(OverlayLayer.Gear, null, RenderMode.Cloth)]
    [InlineData(OverlayLayer.Gear, "characterscroll.shpk", RenderMode.Glow)]
    public void ModeOf_MapsLayerShader(OverlayLayer layer, string? shader, RenderMode expected)
        => Assert.Equal(expected, RenderModeInference.ModeOf(layer, shader));

    // ── ShouldPromoteToGear — what the compositor and the editor both gate on ───

    [Fact]
    public void Promote_GlowOnSkinOverlay_NeedsShell()
        => Assert.True(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Emissive = 0.5f }), aboveGear: false));

    [Fact]
    public void Promote_GlowButPinned_StaysSkin()   // the user's pin outranks the inference
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: true,
            Rows(new ColorTableSubRowPreset { Emissive = 0.5f }), aboveGear: false));

    [Fact]
    public void Promote_ZeroGlow_StaysSkin()
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Emissive = 0f }), aboveGear: false));

    [Fact]
    public void Promote_PlainSkinAboveGear_StillPromotes()   // the pre-existing stacking reason
        => Assert.True(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(), aboveGear: true));

    [Fact]
    public void Promote_AlreadyGear_IsNotPromotedAgain()
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Gear, pinned: false,
            Rows(new ColorTableSubRowPreset { Emissive = 0.5f }), aboveGear: true));

    // A shell is cut from the body, so a face overlay has nothing to be promoted ONTO. Both reasons are
    // vetoed — promoting one anyway built a body shell carrying the face's art.
    [Fact]
    public void Promote_GlowWithNoShellSurface_StaysSkin()
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Emissive = 0.5f }), aboveGear: false, canShell: false));

    [Fact]
    public void Promote_AboveGearWithNoShellSurface_StaysSkin()
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(), aboveGear: true, canShell: false));

    // ── A print is never promoted ──────────────────────────────────────────────
    // It has no coverage of its own to cut a shell from, and a shell would take it away from the very
    // layers it exists to colour. It qualifies on every promotion route otherwise, so each needs vetoing.

    [Fact]
    public void Promote_PrintAboveGear_StaysSkin()
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Blend = RowBlend.Multiply }), aboveGear: true));

    [Fact]
    public void Promote_PrintWithGlowRow_StaysSkin()
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Blend = RowBlend.Screen, Emissive = 0.5f }), aboveGear: false));

    /// <summary>
    /// The one that would have been most visible: a toe cap anywhere in the look promotes every shellable
    /// skin overlay, which would have turned an opaque full-body print into a rainbow bodysuit.
    /// </summary>
    [Fact]
    public void Promote_PrintWithToeCapWanted_StaysSkin()
        => Assert.False(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Blend = RowBlend.Multiply }), aboveGear: false,
            canShell: true, needsUnmirroredShell: false, toeCapWanted: true));

    /// <summary>A row that paints is untouched by any of this.</summary>
    [Fact]
    public void Promote_PaintRowAboveGear_StillPromotes()
        => Assert.True(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Blend = RowBlend.Paint }), aboveGear: true));

    /// <summary>
    /// Mixed rows are not a print: one painting sub-row means the option really does lay down a surface,
    /// and the safe direction is to keep it.
    /// </summary>
    [Fact]
    public void Promote_MixedPaintAndPrintRows_StillPromotes()
        => Assert.True(RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned: false,
            Rows(new ColorTableSubRowPreset { Blend = RowBlend.Multiply },
                 new ColorTableSubRowPreset { Blend = RowBlend.Paint }), aboveGear: true));

    // ── Override path (design binding) reads the override, not the descriptor ───

    [Fact]
    public void Override_ScrollDrivesGlow()
    {
        var ovr = new GearSettingsPreset { Scroll = "neon.tex" };
        Assert.Equal(RenderMode.Glow,
            RenderModeInference.Infer(Rows(), new[] { Desc() }, ovr, RenderMode.Skin, FeatureEdit.Glow));
    }
}
