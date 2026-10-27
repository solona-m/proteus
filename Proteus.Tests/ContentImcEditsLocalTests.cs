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
        var off = ContentImcVisibility.For(null, edits, modelRel, attrs, sel("Off"), (_, _) => null);
        Assert.Equal(["atr_sv_a"], off.Hidden!.Order());
        var on = ContentImcVisibility.For(null, edits, modelRel, attrs, sel("On"), (_, _) => null);
        Assert.Null(on.Hidden);
        Assert.True(on.DropVariantAttributes);
    }

    /// <summary>[AC-imported-imc-toggles-4] The set-and-slot reading of the game's IMC file agrees with the model-path one.</summary>
    [LocalDataFact(LocalData.GameData)]
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
}
