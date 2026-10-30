using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="ContentImcEdits"/> against a real installed mod and the game's own IMC files: the synthetic packs are only
/// as right as their guess at the shape Penumbra's TexTools import writes.
/// </summary>
public class ContentImcEditsLocalTests
{
    /// <summary>
    /// [AC-imported-imc-toggles-1] [EDGE-imported-imc-toggles-17] [EDGE-imported-imc-toggles-35] BWM's "Stocking
    /// Toggle" — a Single group whose options carry an Imc edit beside a same-set Eqp — hides exactly the stocking,
    /// <c>atr_sv_a</c>, on its real model, and nothing the game drives otherwise.
    /// </summary>
    [LocalDataFact(LocalData.ImcToggleMod)]
    public void A_real_installed_mods_imc_toggle_hides_exactly_its_part()
    {
        var root = LocalData.Path(LocalData.ImcToggleMod);
        var (edits, error) = ContentImcEdits.ReadInstalled(root);
        Assert.Null(error);
        Assert.NotNull(edits);

        var feet = new ImcItem('e', 666, "Feet");
        Assert.True(edits!.Names(feet));
        Assert.Equal(1, edits.GoverningVariant(feet));

        var model = Directory.EnumerateFiles(root, "c0201e0666_sho.mdl", SearchOption.AllDirectories).First();
        var modelRel = Path.GetRelativePath(root, model);
        Assert.Equal(feet, ContentImcEdits.ItemOf(modelRel));

        var sel = (string choice) => new Dictionary<string, List<string>> { ["Stocking Toggle"] = [choice] };
        Assert.Equal((ushort)1022, edits.Active(feet, sel("Off"))!.Mask);
        Assert.Equal((ushort)1023, edits.Active(feet, sel("On"))!.Mask);

        var attrs = SecondSkinWriter.AttributeNames(File.ReadAllBytes(model)).ToList();
        Assert.Contains("atr_sv_a", attrs);
        var off = ContentImcVisibility.For(null, edits, modelRel, null, attrs, sel("Off"), (_, _) => null);
        Assert.Equal(["atr_sv_a"], off.Hidden!.Order());
        var on = ContentImcVisibility.For(null, edits, modelRel, null, attrs, sel("On"), (_, _) => null);
        Assert.Null(on.Hidden);
        Assert.True(on.DropVariantAttributes);
    }

    /// <summary>[AC-imported-imc-toggles-4] The set-and-slot reading of the game's IMC file agrees with the model-path one.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Speed", "Slow")]
    public void FromGame_by_set_and_slot_reads_a_real_entry()
    {
        var game = new GameData(LocalData.Path(LocalData.GameData));
        byte[]? Read(string p) => game.GetFile(p)?.Data;

        foreach (var (model, tag, label) in new[]
                 {
                     ("chara/equipment/e0666/model/c0201e0666_sho.mdl", "e0666", "Feet"),
                     ("chara/equipment/e0001/model/c0201e0001_top.mdl", "e0001", "Body"),
                     ("chara/accessory/a0053/model/c0101a0053_rir.mdl", "a0053", "Right ring"),
                 })
        {
            var byPath = ImcEntrySource.FromGame(Read, model, 1);
            Assert.NotNull(byPath);
            Assert.Equal(byPath, ImcEntrySource.FromGame(Read, tag, label, 1));
        }
    }

    /// <summary>
    /// [AC-imc-group-piece-slot-1] [AC-imc-group-piece-slot-2] [AC-imc-group-piece-slot-4] The import from Nao's report,
    /// on its real sidecar and models: with the slot unread it gives exactly her log's hidden parts, and read, each
    /// ring's selection reaches only its own piece.
    /// </summary>
    [LocalDataFact(LocalData.NeolitheRingMod)]
    public void Nao_s_real_ring_import_shows_each_rings_selection_on_its_own_piece()
    {
        var root = LocalData.Path(LocalData.NeolitheRingMod);
        var meta = SidecarDiscoveryService.TryReadMetadata(root);
        Assert.NotNull(meta);

        const string ChestModel = "chest/neolithe nsfw hazelnut s.mdl";
        const string LegsModel = "legs/neolithe gen b small.mdl";
        var pieces = meta!.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces).ToList();
        Assert.Equal("Right ring", pieces.First(p => p.ModelFor("0201") == ChestModel).Slot);
        Assert.Equal("Left ring", pieces.First(p => p.ModelFor("0201") == LegsModel).Slot);

        var groups = meta.ContentAttributes!;
        var chestGroup = Assert.Single(groups, g => g.Group == "Chest piercings" && g.Slot == "RFinger");
        var legsGroup = Assert.Single(groups, g => g.Group == "Legs piercings" && g.Slot == "LFinger");

        string[] Parts(int n) => [.. Enumerable.Range(0, n).Select(i => $"atr_rv_{(char)('a' + i)}")];
        var chestAttrs = SecondSkinWriter.AttributeNames(File.ReadAllBytes(Path.Combine(root, ChestModel))).ToList();
        var legsAttrs = SecondSkinWriter.AttributeNames(File.ReadAllBytes(Path.Combine(root, LegsModel))).ToList();
        Assert.Equal(Parts(8).Concat(["atr_hij", "atr_nek", "atr_ude"]).Order(StringComparer.Ordinal),
            chestAttrs.Order(StringComparer.Ordinal));
        Assert.Equal(Parts(7).Concat(["atr_hiz", "atr_sne"]).Order(StringComparer.Ordinal),
            legsAttrs.Order(StringComparer.Ordinal));

        // Every option of both groups ticked, named in full so neither group's defaults (81, 1) can stand in.
        var allOptionsOfBothImcGroups = new Dictionary<string, List<string>>
        {
            [chestGroup.Group] = [.. chestGroup.Options.Keys],
            [legsGroup.Group] = [.. legsGroup.Options.Keys],
        };
        string[] Hidden(string model, string? slot, IReadOnlyList<string> attrs,
                        IReadOnlyList<ContentAttributeGroup> gs, IReadOnlyDictionary<string, List<string>> sel)
            => [.. (ContentPieceResolver.HiddenAttributes(gs, model, slot, attrs, sel) ?? new HashSet<string>()).Order()];

        // (a) The slot unread: Nao's log, verbatim.
        Assert.Equal(["atr_rv_a", "atr_rv_b", "atr_rv_c", "atr_rv_d", "atr_rv_h"],
            Hidden(ChestModel, null, chestAttrs, groups, allOptionsOfBothImcGroups));
        Assert.Equal(["atr_rv_a", "atr_rv_b", "atr_rv_c", "atr_rv_d"],
            Hidden(LegsModel, null, legsAttrs, groups, allOptionsOfBothImcGroups));

        // (b) Read: everything on the chest shows; the legs lose only the bits their own ring never shows.
        Assert.Empty(Hidden(ChestModel, "Right ring", chestAttrs, groups, allOptionsOfBothImcGroups));
        Assert.Equal(["atr_rv_a", "atr_rv_b", "atr_rv_c", "atr_rv_d"],
            Hidden(LegsModel, "Left ring", legsAttrs, groups, allOptionsOfBothImcGroups));

        // (c) A partial selection: each piece exactly as its own group alone decides.
        var collarAndChestDermalsWithHipDermals = new Dictionary<string, List<string>>
        {
            [chestGroup.Group] = ["Collar bones dermals", "Chest dermals"],
            [legsGroup.Group] = ["Hips dermals"],
        };
        var chest = Hidden(ChestModel, "Right ring", chestAttrs, groups, collarAndChestDermalsWithHipDermals);
        Assert.Equal(["atr_rv_c", "atr_rv_d", "atr_rv_e", "atr_rv_f", "atr_rv_g", "atr_rv_h"], chest);
        Assert.Equal(Hidden(ChestModel, "Right ring", chestAttrs, [chestGroup], collarAndChestDermalsWithHipDermals), chest);
        var legs = Hidden(LegsModel, "Left ring", legsAttrs, groups, collarAndChestDermalsWithHipDermals);
        Assert.Equal(["atr_rv_a", "atr_rv_b", "atr_rv_c", "atr_rv_d", "atr_rv_f", "atr_rv_g"], legs);
        Assert.Equal(Hidden(LegsModel, "Left ring", legsAttrs, [legsGroup], collarAndChestDermalsWithHipDermals), legs);

        // (d) The log names each piece's own group; (e) and has nothing to warn about.
        Assert.Equal("imc group Chest piercings", ContentImcVisibility.For(groups, null, ChestModel, "Right ring",
            chestAttrs, allOptionsOfBothImcGroups, (_, _) => null).Why);
        Assert.Equal("imc group Legs piercings", ContentImcVisibility.For(groups, null, LegsModel, "Left ring",
            legsAttrs, allOptionsOfBothImcGroups, (_, _) => null).Why);
        Assert.Equal(PieceSlotNotice.None, ContentPieceResolver.SlotNotice(groups, ChestModel, "Right ring"));
        Assert.Equal(PieceSlotNotice.None, ContentPieceResolver.SlotNotice(groups, LegsModel, "Left ring"));
    }
}
