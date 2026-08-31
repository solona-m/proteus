using System;
using System.Collections.Generic;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What an EMPTY colour table means on a shell, which differs by how the overlay got there.
/// <para/>
/// A shell multiplies its art by its colour table, and <see cref="GearMaterialWriter.Build"/> leaves the
/// cloned vanilla template's table in place when it is handed null rows. For an overlay someone deliberately
/// authored as cloth that is right — the template belongs to the look being worn. For one AUTO-PROMOTED from
/// Skin it is not: nobody chose gear, nobody chose colours, and the art would have rendered at its authored
/// colour on the skin layer. The vanilla top Proteus clones (e0041) ships pink, olive and brown rows, so
/// inheriting them renders a skin overlay as dark patches wherever the index selects one.
/// </summary>
public class PromotedShellRowsTests
{
    /// <summary>
    /// The template's table is genuinely not neutral — the whole reason inheriting it is a bug. Built from
    /// the same row shape <see cref="GearMaterialWriter"/> writes, so this states the premise rather than
    /// depending on a game install.
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
    /// The flag is transient by design: it describes what the COMPOSITOR did this run, not anything an
    /// author wrote, so it must never reach metadata.json — an exported mod carrying it would claim to be
    /// promoted on someone else's character.
    /// </summary>
    [Fact]
    public void PromotedFromSkin_is_never_serialized()
    {
        var d = new OverlayDescriptor
        {
            Layer = OverlayLayer.Gear,
            PromotedFromSkin = true,
            MaterialGamePaths = ["chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl"],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(d, ProteusJson.MetadataWrite);
        Assert.DoesNotContain("PromotedFromSkin", json, StringComparison.OrdinalIgnoreCase);

        // And it does not survive a round trip, which is why the compositor sets it AFTER cloning.
        var back = System.Text.Json.JsonSerializer.Deserialize<OverlayDescriptor>(json, ProteusJson.MetadataRead)!;
        Assert.False(back.PromotedFromSkin);
    }

    /// <summary>
    /// A promoted overlay is still an ordinary gear overlay in every other respect — the flag must not leak
    /// into the mask-shell behaviour, which also linearises its diffuse.
    /// </summary>
    [Fact]
    public void Promotion_is_independent_of_the_mask_shell_flag()
    {
        var d = new OverlayDescriptor { PromotedFromSkin = true };
        Assert.False(d.IsMaskShell);

        var m = new OverlayDescriptor { IsMaskShell = true };
        Assert.False(m.PromotedFromSkin);
    }
}
