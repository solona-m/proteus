using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The ear-skin rule on real Miqo'te hairstyles, against the vanilla face. What each one must keep was read by hand
/// from the model (2026-10-16): which mesh is the ear, and that its skin rides the head bone — not from the rule itself.
/// </summary>
public class HatCompatEarSkinLocalTests
{
    private const string Umbra = LocalData.Mods + ":CO Umbra w Scrunchie/items/chara/human/c0801/obj/hair/h0115/model/c0801h0115_hir.mdl";
    private const string PowerHair = LocalData.Mods
        + ":[Yuria] Power Hair (162)/3D Install - Miqo'te/Long hair - longer bang/chara/human/c0801/obj/hair/h0162/model/c0801h0162_hir.mdl";
    private const string Kikiro = LocalData.Mods + ":Commission AVA Kikiro Hair";
    private const string KikiroRel = "hair kikiro/007/chara/human/c0801/obj/hair/h0007/model/c0801h0007_hir.mdl";

    private static HatCompatSolve.Result Solve(byte[] hair)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var head = data.GetFile("chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl")!.Data;
        return HatCompatSolve.Solve(hair, ModelPartReader.Read(hair)!, head, raceCode: "0801");
    }

    /// <summary>The author's file: Proteus's backup where the file in the mod is already fitted.</summary>
    private static byte[] Author(string modRoot, string rel)
    {
        var backup = Path.Combine(modRoot, "Proteus", "hatcompat-backup", rel.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllBytes(File.Exists(backup) ? backup : Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Umbra's ear is mesh 1: fur, and 26 head-bone triangles of skin around the ear's root.</summary>
    [LocalDataFact(LocalData.GameData, Umbra)]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    [Trait("Plan", "AC-miqote-ear-skin-2")]
    [Trait("Plan", "EDGE-miqote-ear-skin-2")]
    public void UmbrasEarKeepsItsSkinUnderAHat()
    {
        var cut = Solve(File.ReadAllBytes(LocalData.Path(Umbra))).Cut;
        Assert.DoesNotContain(cut, p => p.Mesh == 1);
        Assert.NotEmpty(cut);                     // the hair itself still goes under the hat
    }

    /// <summary>Power Hair's ear is mesh 3: 60 head-bone triangles of skin beside the fur.</summary>
    [LocalDataFact(LocalData.GameData, PowerHair)]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    public void PowerHairsEarKeepsItsSkinUnderAHat()
    {
        var cut = Solve(File.ReadAllBytes(LocalData.Path(PowerHair))).Cut;
        Assert.DoesNotContain(cut, p => p.Mesh == 3);
        Assert.NotEmpty(cut);
    }

    /// <summary>
    /// Kikiro welds its scalp into the fur's submesh (4,774 non-ear triangles beside the fur). The ear-skin rule must not
    /// read that as skin: the hair above the rim is still cut.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Kikiro)]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    [Trait("Plan", "EDGE-miqote-ear-skin-1")]
    public void KikirosWeldedHairIsStillCut()
    {
        var cut = Solve(Author(LocalData.Path(Kikiro), KikiroRel)).Cut;
        int triangles = cut.Sum(p => p.Island < 0 ? 1000 : p.Ordinals.Length);
        Assert.True(triangles >= 2000, $"only {triangles} triangles cut");
    }
}
