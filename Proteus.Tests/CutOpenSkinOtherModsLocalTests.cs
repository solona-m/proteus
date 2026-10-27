using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Other mods' garments refitted the automatic refit's way (see <see cref="CodeineDressHolesLocalTests.Refit"/>), with the
/// skin the cut leaves out in the open drawn (see <see cref="BodyRetarget.GrowSpannedOpenSkin"/>) and without: onto Muse+
/// "Defined - Gen A", the body Codeine was reported on, and onto the bodies their own regression tests refit them to.
/// Where a garment reaches onto a slot it does not lead, no more of the bodies' skin is left out in the open and no more
/// comes through the cloth; where it reaches onto none, the refit is the one it was, byte for byte.
/// </summary>
public class CutOpenSkinOtherModsLocalTests(ITestOutputHelper output)
{
    private const string Dresses = LocalData.Mods + ":I Just Really Like Sweater Dresses, OK - by Solona";
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    private const string OldThing = LocalData.Mods + ":This Old Thing - by Solona";
    private const string Sirius = LocalData.Mods + ":Sirius - by Solona";
    private const string Laurel = LocalData.Mods + ":[Jaque] Laurel Attire [July 2025]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Muse = CodeineDressHolesLocalTests.Muse;

    /// <summary>Each garment: its mod (or <see cref="LocalData.DeiGratiaPack"/>), its model, its slot, and the body its own
    /// regression test refits it onto.</summary>
    public static TheoryData<string, string, string, string> Garments => new()
    {
        { Dresses, "size/medium/chara/equipment/e6057/model/c0201e6057_top.mdl", "_top", Rue },
        { Geometric, "leg size/small/chara/equipment/e6010/model/c0201e6010_dwn.mdl", "_dwn", Rue },
        { OldThing, "size/rue m/chara/equipment/e6255/model/c0201e6255_top.mdl", "_top", Neolithe },
        { Sirius, "size/Sirius/L/chara/equipment/e0154/model/c0201e0154_top.mdl", "_top", Yab },
        { Laurel, "valentione emissary/bottom/chara/equipment/e6144/model/c0201e6144_dwn.mdl", "_dwn", Rue },
        { LocalData.DeiGratiaPack, "chara/equipment/e9068/model/c0201e9068_top.mdl", "_top", Bibo },
    };

    private static byte[] Read(string mod, string model)
        => mod == LocalData.DeiGratiaPack
            ? LocalData.PackEntryEndingWith(mod, model)
            : File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), model));

    [LocalDataTheory(Dresses, Geometric, OldThing, Sirius, Laurel, LocalData.DeiGratiaPack, Bibo, Neolithe, Rue, Yab, Muse,
                     LocalData.UvMaps)]
    [MemberData(nameof(Garments))]
    [Trait("Plan", "AC-cut-open-skin-2")]
    [Trait("Plan", "AC-cut-open-skin-3")]
    public void Other_garments_are_no_worse(string mod, string model, string slot, string ownBody)
    {
        byte[] bytes = Read(mod, model);
        int defined = Body(Muse).For("_dwn", "0201").ToList().FindIndex(o => o.Label == "Defined - Gen A");
        foreach (var (body, legs) in new[] { (Muse, defined), (ownBody, 0) })
        {
            string what = $"{Path.GetFileName(LocalData.Path(mod))} {slot} -> {Path.GetFileName(LocalData.Path(body))}";
            if (CodeineDressHolesLocalTests.Refit(bytes, body, 0, legs, slot) == null)
            {
                output.WriteLine($"{what}: not read");
                continue;
            }
            var (now, before) = CodeineDressHolesLocalTests.NoWorse(output, what, bytes, body, 0, legs, slot);
            // Nothing drawn in the open: the refit it was.
            if (now.Swap?.Spanned is null or 0) Assert.Equal(before.Model, now.Model);
        }
    }

    private static BodySizeCatalog Body(string need) => BodySizeCatalog.Read(LocalData.Path(need));
}
