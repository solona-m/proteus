using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SyntheticPack;

namespace Proteus.Tests;

/// <summary>A two-ring pack's toggle groups, imported, each judging only its own ring's piece.</summary>
public partial class ContentImportTests
{
    /// <summary>
    /// [AC-imc-group-piece-slot-3] Import → sidecar → visibility: the pieces record their ring, and each ring's
    /// <c>Imc</c> group decides its own piece alone although both models sit in the archive off a game path.
    /// </summary>
    [Fact]
    public void An_imported_two_ring_pack_judges_each_piece_by_its_own_rings_group()
    {
        using var pack = RingImcGroups();
        var sidecar = ContentImportService.BuildSidecar(ContentImportService.Inspect(pack.Path), "Synthetic", "Synthetic");

        var pieces = sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces).ToList();
        var chest = Assert.Single(pieces, p => p.ModelFor("0201") == "chest/chest piece.mdl");
        var legs = Assert.Single(pieces, p => p.ModelFor("0201") == "legs/legs piece.mdl");
        Assert.Equal("Right ring", chest.Slot);
        Assert.Equal("Left ring", legs.Slot);

        var groups = sidecar.ContentAttributes!;
        Assert.Equal(["LFinger", "RFinger"], groups.Select(g => g.Slot!).Order());
        Assert.All(groups, g => Assert.Equal(103, g.SetId));

        string[] chestAttrs = [.. Enumerable.Range(0, 8).Select(i => $"atr_rv_{(char)('a' + i)}")];
        string[] legsAttrs = [.. Enumerable.Range(0, 7).Select(i => $"atr_rv_{(char)('a' + i)}")];
        var all = groups.ToDictionary(g => g.Group, g => g.Options.Keys.ToList());
        var hips = new Dictionary<string, List<string>> { ["Legs piercings"] = ["Hips dermals"],
                                                           ["Chest piercings"] = [.. all["Chest piercings"]] };

        var onChest = ContentImcVisibility.For(groups, null, chest.ModelFor("0201")!, chest.Slot, chestAttrs, all, (_, _) => null);
        Assert.Equal(ImcMaskSource.ImcGroup, onChest.Source);
        Assert.Null(onChest.Hidden);   // everything ticked: nothing the left ring lacks reaches the chest

        var onLegs = ContentImcVisibility.For(groups, null, legs.ModelFor("0201")!, legs.Slot, legsAttrs, hips, (_, _) => null);
        Assert.Equal(ImcMaskSource.ImcGroup, onLegs.Source);
        Assert.Equal(["atr_rv_a", "atr_rv_b", "atr_rv_c", "atr_rv_d", "atr_rv_f", "atr_rv_g"], onLegs.Hidden!.Order());
    }
}
