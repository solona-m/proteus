using System.Collections.Generic;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="SecondSkinService.BuildRows"/> — the metadata presets turned into the 32 sub-rows a SHELL
/// material gets.
/// <para/>
/// What these guard is that "white" means two different things depending on the shell. Over an ordinary
/// shell's own art it is a neutral multiply, the same as an absent row on the skin layer. Over a MASK
/// shell, whose base texture is white and whose colour lives entirely in the colorset, it is paint — so a
/// row left white there is a row the shell will actually draw white.
/// </summary>
public class ShellColorRowTests
{
    private static ColorTableRowPreset Row(int row, string? a, string? b) => new()
    {
        Row = row,
        SubRowA = a == null ? null : new ColorTableSubRowPreset { Diffuse = a },
        SubRowB = b == null ? null : new ColorTableSubRowPreset { Diffuse = b },
    };

    private static (float R, float G, float B)? Diffuse(Dictionary<int, GearColorRow> rows, int subRow)
        => rows[subRow].Diffuse;

    // ── mask shells mirror a half-authored pair ──────────────────────────────

    [Fact]
    public void MaskShell_SubRowAOnly_MirrorsIntoB()
    {
        // The reported bug: MaskColorTableRows carried Row 1 with SubRowA #1B1B1B and no SubRowB, so the
        // shader's B→A lerp painted the unset half WHITE over a mask shell's white base — a white fringe
        // tracing every mask edge, on a colorset whose every visible swatch was black.
        var rows = SecondSkinService.BuildRows(
            new List<ColorTableRowPreset> { Row(1, "#1B1B1B", null) }, isMaskShell: true)!;
        var expected = (0x1B / 255f, 0x1B / 255f, 0x1B / 255f);
        Assert.Equal(expected, Diffuse(rows, 0));
        Assert.Equal(expected, Diffuse(rows, 1));
    }

    [Fact]
    public void MaskShell_SubRowBOnly_MirrorsIntoA()
    {
        var rows = SecondSkinService.BuildRows(
            new List<ColorTableRowPreset> { Row(16, null, "#000000") }, isMaskShell: true)!;
        Assert.Equal((0f, 0f, 0f), Diffuse(rows, 30));
        Assert.Equal((0f, 0f, 0f), Diffuse(rows, 31));
    }

    // ── ordinary shells do not ───────────────────────────────────────────────

    [Fact]
    public void FabricShell_SubRowAOnly_LeavesBNeutral()
    {
        // A fabric shell carries its colour in the base texture and expects a neutral colorset, so white is
        // a no-op multiply. Mirroring here would newly TINT the art at every green < 255 texel — a change to
        // how existing mods render, well outside the mask-fringe bug.
        var rows = SecondSkinService.BuildRows(new List<ColorTableRowPreset> { Row(16, "#FF0000", null) })!;
        Assert.Equal((1f, 0f, 0f), Diffuse(rows, 30));
        Assert.Equal((1f, 1f, 1f), Diffuse(rows, 31));
    }

    [Fact]
    public void FabricShell_GlowOnOneSubRow_DoesNotSpreadToTheOther()
    {
        // Mirroring carries Emissive across too, so gating it also keeps an A-only glow from newly lighting
        // the B half of every pair.
        var rows = SecondSkinService.BuildRows(new List<ColorTableRowPreset>
        {
            new() { Row = 16, SubRowA = new ColorTableSubRowPreset { Diffuse = "#FFFFFF", Emissive = 1f } },
        })!;
        Assert.Equal((1f, 1f, 1f), rows[30].Emissive);
        Assert.Equal((0f, 0f, 0f), rows[31].Emissive);
    }

    // ── shared by both ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UntouchedPairs_StayNeutralWhite(bool isMaskShell)
    {
        // Mirroring is scoped to a pair the author listed. Every pair they did not stays at the white
        // baseline, so no row inherits the vanilla template's (often dark) colour.
        var rows = SecondSkinService.BuildRows(
            new List<ColorTableRowPreset> { Row(16, "#123456", null) }, isMaskShell)!;
        Assert.Equal(32, rows.Count);
        for (int r = 0; r < 30; r++)
            Assert.Equal((1f, 1f, 1f), Diffuse(rows, r));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothSubRows_KeptIndependent(bool isMaskShell)
    {
        var rows = SecondSkinService.BuildRows(
            new List<ColorTableRowPreset> { Row(16, "#FF0000", "#00FF00") }, isMaskShell)!;
        Assert.Equal((1f, 0f, 0f), Diffuse(rows, 30));
        Assert.Equal((0f, 1f, 0f), Diffuse(rows, 31));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StockExport_SubRowAWhiteOnly_Unchanged(bool isMaskShell)
    {
        // What the exporter actually writes for nearly every shipped option. Mirroring white into white has
        // to be a no-op, or every existing mod's shell textures rehash and re-upload.
        var rows = SecondSkinService.BuildRows(
            new List<ColorTableRowPreset> { Row(16, "#FFFFFF", null) }, isMaskShell)!;
        Assert.Equal((1f, 1f, 1f), Diffuse(rows, 30));
        Assert.Equal((1f, 1f, 1f), Diffuse(rows, 31));
    }

    [Fact]
    public void PresetWithNoColour_StaysWhiteRatherThanFallingBackToTheTemplate()
    {
        // The editor materialises a blank sub-row the moment any non-colour field is touched. Assigning
        // REPLACES the neutral entry, so without a fallback the diffuse would be null — and PatchColorTable
        // skips null fields, letting the vanilla template's colour through.
        var rows = SecondSkinService.BuildRows(new List<ColorTableRowPreset>
        {
            new() { Row = 16, SubRowA = new ColorTableSubRowPreset { Roughness = 0.25f } },
        })!;
        Assert.Equal((1f, 1f, 1f), Diffuse(rows, 30));
        Assert.Equal(0.25f, rows[30].Roughness);
    }

    [Fact]
    public void GlowWithNoColourAnywhere_StaysDark()
    {
        // The white diffuse fallback must not leak into the emissive: a row carrying an intensity but no
        // colour is deliberately dark (see RowFrom), and resolving it white made shipped mods blow out.
        var rows = SecondSkinService.BuildRows(new List<ColorTableRowPreset>
        {
            new() { Row = 16, SubRowA = new ColorTableSubRowPreset { Emissive = 1f } },
        })!;
        Assert.Equal((0f, 0f, 0f), rows[30].Emissive);
    }

    [Fact]
    public void NoPresets_NeutralOnlyForAMaskShell()
    {
        // Null means "keep the gear template's own table", which is right for an ordinary shell and wrong
        // for a mask shell, which has no look of its own.
        Assert.Null(SecondSkinService.BuildRows(null));
        Assert.Null(SecondSkinService.BuildRows(new List<ColorTableRowPreset>()));
        var mask = SecondSkinService.BuildRows(new List<ColorTableRowPreset>(), isMaskShell: true)!;
        Assert.Equal(32, mask.Count);
        Assert.All(mask.Values, r => Assert.Equal((1f, 1f, 1f), r.Diffuse));
    }

    [Fact]
    public void SparseRows_DoNotMirror()
    {
        // A content pack's own material is the author's: a half they did not set must be left unwritten so
        // whatever they shipped survives, not overwritten with the other half.
        var rows = SecondSkinService.BuildSparseRows(
            new List<ColorTableRowPreset> { Row(1, "#1B1B1B", null) })!;
        Assert.True(rows.ContainsKey(0));
        Assert.False(rows.ContainsKey(1));
    }

    /// <summary>
    /// The weave reaches both builders. Neither knows about it directly — both go through
    /// <c>RowFrom</c>, which is what stops a shell and a content material disagreeing about what a preset
    /// means, and this is what keeps that true as fields are added.
    /// </summary>
    [Fact]
    public void TileCarriesThroughBothRowBuilders()
    {
        var preset = new List<ColorTableRowPreset>
        {
            new()
            {
                Row = 1,
                SubRowA = new ColorTableSubRowPreset
                {
                    Diffuse = "#FFFFFF", Tile = 9, TileStrength = 0.5f, TileScaleU = 8f, TileScaleV = 4f,
                },
            },
        };

        foreach (var rows in new[] { SecondSkinService.BuildRows(preset)!, SecondSkinService.BuildSparseRows(preset)! })
        {
            Assert.Equal(9, rows[0].TileIndex);
            Assert.Equal(0.5f, rows[0].TileStrength);
            Assert.Equal(8f, rows[0].TileScaleU);
            Assert.Equal(4f, rows[0].TileScaleV);
        }
    }

    /// <summary>
    /// A mask shell mirrors a half-authored pair, and the weave has to travel with it. The mirror exists
    /// because the shader lerps B toward A — a weave on one half only would fringe along the seam exactly
    /// as an unmirrored colour does.
    /// </summary>
    [Fact]
    public void MaskShellMirrorsTheWeave()
    {
        var rows = SecondSkinService.BuildRows(
            new List<ColorTableRowPreset>
            {
                new() { Row = 1, SubRowA = new ColorTableSubRowPreset { Diffuse = "#FFFFFF", Tile = 5 } },
            },
            isMaskShell: true)!;

        Assert.Equal(5, rows[0].TileIndex);
        Assert.Equal(5, rows[1].TileIndex);
    }
}
