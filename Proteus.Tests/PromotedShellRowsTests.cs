using System.Collections.Generic;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What an EMPTY colour table means on a shell.
/// <para/>
/// A shell multiplies its art by its colour table, and <see cref="GearMaterialWriter.Build"/> leaves the
/// cloned template's table in place when it is handed null rows. That is never what anyone wants here: the
/// template is a FIXED vanilla top (e0041), unrelated to whatever the character is actually wearing, and it
/// ships pink, olive and brown rows with a near-black pair 16 — the very pair Proteus treats as the neutral
/// "unclaimed" selector, and the one a tattoo's index usually names. Inheriting it renders the author's art
/// at a fraction of its authored brightness, while the identical art on the SKIN layer renders untinted.
/// <para/>
/// So every shell with no authored rows takes the neutral-white baseline instead, whether it was
/// deliberately made cloth or auto-promoted from skin. An author who set even one row is unaffected —
/// <see cref="SecondSkinService.BuildRows"/> already starts those from the same baseline.
/// </summary>
public class PromotedShellRowsTests
{
    /// <summary>
    /// The baseline is a no-op multiply on every one of the 32 sub-rows, so art that carries its own colour
    /// comes through at exactly the value it was authored at.
    /// </summary>
    [Fact]
    public void The_neutral_baseline_is_white_on_every_row()
    {
        var rows = SecondSkinService.NeutralRows();
        // 16 pairs = 32 sub-rows; every one of them must be a no-op multiply over the shell's own art.
        Assert.Equal(32, rows.Count);
        foreach (var (_, row) in rows)
        {
            Assert.NotNull(row.Diffuse);
            var (r, g, b) = row.Diffuse!.Value;
            Assert.Equal(1f, r);
            Assert.Equal(1f, g);
            Assert.Equal(1f, b);
        }
    }

    /// <summary>
    /// An empty preset list takes the baseline — this is the "cloth is a lot darker than skin" fix. The
    /// argument is passed unconditionally by the shell build, so the answer must not depend on how the
    /// overlay reached the gear layer.
    /// </summary>
    [Fact]
    public void No_authored_rows_takes_the_neutral_baseline()
    {
        foreach (var presets in new List<ColorTableRowPreset>?[] { null, [] })
        {
            var rows = SecondSkinService.BuildRows(presets, isMaskShell: false, neutralWhenEmpty: true);
            Assert.NotNull(rows);
            Assert.Equal(32, rows!.Count);
            Assert.Equal((1f, 1f, 1f), rows[30].Diffuse);   // pair 16 sub-row A — near-black in the template
            Assert.Equal((1f, 1f, 1f), rows[31].Diffuse);   // pair 16 sub-row B — the one a tattoo's _id names
        }
    }

    /// <summary>
    /// The narrow blast radius, stated: authoring ONE row already neutralised every other pair, so this
    /// change cannot have moved a shell whose author picked any colour at all.
    /// </summary>
    [Fact]
    public void One_authored_row_already_left_the_others_neutral()
    {
        var presets = new List<ColorTableRowPreset>
        {
            new() { Row = 1, SubRowA = new ColorTableSubRowPreset { Diffuse = "#FF0000" } },
        };

        var withFlag    = SecondSkinService.BuildRows(presets, isMaskShell: false, neutralWhenEmpty: true);
        var withoutFlag = SecondSkinService.BuildRows(presets, isMaskShell: false, neutralWhenEmpty: false);

        Assert.NotNull(withFlag);
        Assert.NotNull(withoutFlag);
        // The authored pair is the author's either way...
        Assert.Equal(withoutFlag![0].Diffuse, withFlag![0].Diffuse);
        // ...and every pair they did not name was already white before the flag existed.
        Assert.Equal((1f, 1f, 1f), withoutFlag[31].Diffuse);
        Assert.Equal((1f, 1f, 1f), withFlag[31].Diffuse);
    }

    /// <summary>
    /// Neutral means EVERY field the colour table can carry, not just the colour. PatchColorTable skips null
    /// fields, so anything left unset here is silently the vanilla template's — and e0041's pair 16 ships
    /// specular 0.64/0.36 while other rows carry metalness 1.0 and specular 1.44. Neutralising the diffuse
    /// alone left the art un-tinted but rendered through a duller, sometimes metallic, surface.
    /// </summary>
    [Fact]
    public void The_baseline_neutralises_every_field_not_only_the_colour()
    {
        var n = SecondSkinService.NeutralRow;
        Assert.Equal((1f, 1f, 1f), n.Diffuse);
        Assert.Equal((0f, 0f, 0f), n.Emissive);
        Assert.Equal((1f, 1f, 1f), n.Specular);
        Assert.Equal(0.5f, n.Roughness);
        Assert.Equal(0f, n.Metalness);
        Assert.Equal(0, n.SphereMapIndex);
        Assert.Equal(0f, n.SphereMapMask);
    }

    /// <summary>
    /// The same defect one level down: RowFrom leaves every field the author did not fill as null, so an
    /// AUTHORED row used to inherit the template's specular/metalness/sphere on the very row someone chose
    /// a colour for. Authoring a colour must not silently opt you into a vanilla top's surface response.
    /// </summary>
    [Fact]
    public void An_authored_row_keeps_the_neutral_values_for_fields_it_left_unset()
    {
        var presets = new List<ColorTableRowPreset>
        {
            // Colour only — no specular, roughness, metalness or sphere map.
            new() { Row = 16, SubRowA = new ColorTableSubRowPreset { Diffuse = "#804020" } },
        };

        var rows = SecondSkinService.BuildRows(presets, isMaskShell: false, neutralWhenEmpty: true);
        var authored = rows![30];   // pair 16 sub-row A

        // The author's colour survives...
        Assert.Equal((0x80 / 255f, 0x40 / 255f, 0x20 / 255f), authored.Diffuse);
        // ...and every field they did not name is neutral, not the template's.
        Assert.Equal((1f, 1f, 1f), authored.Specular);
        Assert.Equal(0.5f, authored.Roughness);
        Assert.Equal(0f, authored.Metalness);
        Assert.Equal(0, authored.SphereMapIndex);
        Assert.Equal(0f, authored.SphereMapMask);
    }

    /// <summary>A field the author DID set still wins over the baseline.</summary>
    [Fact]
    public void An_authored_field_still_overrides_the_baseline()
    {
        var presets = new List<ColorTableRowPreset>
        {
            new()
            {
                Row = 3,
                SubRowA = new ColorTableSubRowPreset { Metalness = 1f, Roughness = 0.2f, Specular = "#FF0000" },
            },
        };

        var rows = SecondSkinService.BuildRows(presets, isMaskShell: false, neutralWhenEmpty: true);
        var authored = rows![4];   // pair 3 sub-row A

        Assert.Equal(1f, authored.Metalness);
        Assert.Equal(0.2f, authored.Roughness);
        Assert.Equal((1f, 0f, 0f), authored.Specular);
        // ...and the colour they never set falls back to the no-tint baseline rather than the template's.
        Assert.Equal((1f, 1f, 1f), authored.Diffuse);
    }

    /// <summary>
    /// A mask shell keeps its extra behaviour — the half-pair mirroring — which the shared baseline must not
    /// hand to an ordinary shell: there the base texture carries the colour, so mirroring would newly tint
    /// the art wherever the index's green is not pinned to the authored side.
    /// </summary>
    [Fact]
    public void Only_a_mask_shell_mirrors_a_half_authored_pair()
    {
        var presets = new List<ColorTableRowPreset>
        {
            new() { Row = 2, SubRowA = new ColorTableSubRowPreset { Diffuse = "#FF0000" } },
        };

        var mask     = SecondSkinService.BuildRows(presets, isMaskShell: true);
        var ordinary = SecondSkinService.BuildRows(presets, isMaskShell: false, neutralWhenEmpty: true);

        // Pair 2 = sub-rows 2 and 3. A is authored on both; B mirrors only on the mask shell.
        Assert.Equal(mask![2].Diffuse, mask[3].Diffuse);
        Assert.Equal((1f, 1f, 1f), ordinary![3].Diffuse);
    }
}
