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
        => Assert.False(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Diffuse = "#FFF", Emissive = 0.5f }));

    [Fact]
    public void NullSubRow_IsNotCloth() => Assert.False(RenderModeInference.IsClothSub(null));

    [Theory]
    [MemberData(nameof(ClothSubRows))]
    public void ClothFeature_IsCloth(ColorTableSubRowPreset sub)
        => Assert.True(RenderModeInference.IsClothSub(sub));

    public static IEnumerable<object[]> ClothSubRows() => new[]
    {
        new object[] { new ColorTableSubRowPreset { Specular = "#808080" } },
        new object[] { new ColorTableSubRowPreset { Roughness = 0.5f } },        // any explicit roughness = intent
        new object[] { new ColorTableSubRowPreset { Metalness = 0.3f } },
        new object[] { new ColorTableSubRowPreset { SphereMap = 4 } },
        new object[] { new ColorTableSubRowPreset { SphereIntensity = 0.7f } },
    };

    [Fact]
    public void ExplicitZeroMetal_IsNotCloth()   // metal 0 / sphere 0 are "off", not a Cloth signal
        => Assert.False(RenderModeInference.IsClothSub(new ColorTableSubRowPreset { Metalness = 0f, SphereMap = 0, SphereIntensity = 0f }));

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

    // ── Override path (design binding) reads the override, not the descriptor ───

    [Fact]
    public void Override_ScrollDrivesGlow()
    {
        var ovr = new GearSettingsPreset { Scroll = "neon.tex" };
        Assert.Equal(RenderMode.Glow,
            RenderModeInference.Infer(Rows(), new[] { Desc() }, ovr, RenderMode.Skin, FeatureEdit.Glow));
    }
}
