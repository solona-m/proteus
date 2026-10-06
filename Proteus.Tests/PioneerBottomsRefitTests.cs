using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Reported: the vanilla Pioneer's Bottoms (e0689 legs) refitted with "take the new body's skin" ticked still drew the
/// vanilla skin — a pale band across the lower back between the coat and the belt. Its skin is one 38-vertex strip;
/// laid onto Rue+ two corners missed the hip by 6 mm (on Neolithe four, by 2-6 mm), and the swap read the strip as a
/// posed piece of the garment's own and kept it. Bibo+ laid every corner and swapped.
/// </summary>
public class PioneerBottomsRefitTests(ITestOutputHelper output)
{
    private const string Bottoms = "chara/equipment/e0689/model/c0201e0689_dwn.mdl";
    private const string VanillaLegs = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    [LocalDataTheory(LocalData.GameData, LocalData.Mods + ":hs-Rue+-2.2.7-y0f", LocalData.Mods + ":Neolithe [ALL IN ONE]",
                     LocalData.Mods + ":Bibo+")]
    [InlineData("hs-Rue+-2.2.7-y0f")]
    [InlineData("Neolithe [ALL IN ONE]")]
    [InlineData("Bibo+")]
    public void The_vanilla_waist_skin_is_swapped_for_the_body_s(string bodyMod)
    {
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        byte[] garmentBytes = game.GetFile(Bottoms)!.Data;
        byte[] sourceBytes = game.GetFile(VanillaLegs)!.Data;

        var catalog = BodySizeCatalog.Read(LocalData.Path(LocalData.Mods + ":" + bodyMod));
        byte[] targetBytes = File.ReadAllBytes(catalog.PathOf(catalog.For("_dwn").First()));
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        Assert.True(BodyCorrespondence.TryBuild(source, Uv(sourceBytes), target, Uv(targetBytes), "_dwn", out var built,
                                                out string refusal, remap), refusal);
        var pairs = new List<BodyRetarget.SlotPair> { new("_dwn", built!, target, targetBytes, sourceBytes) };

        var garment = ModelPartReader.Read(garmentBytes)!;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_dwn", replaceSkin: true, acrossBodies: true);
        output.WriteLine($"{planned.Report.Swap}");

        var swap = planned.Report.Swap!.Value;
        Assert.Equal(30, swap.Removed);
        Assert.Equal(0, swap.Kept);
        var refit = ModelPartReader.Read(planned.Model)!;
        Assert.DoesNotContain(refit.Parts, p => p.Material == "/mt_c0201b0001_a.mtrl");
    }

    private static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null) ? uv : [];
}
