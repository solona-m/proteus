using System;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>Women's shoes and gloves made men's in the size set (mens-ends-crotch), on real bodies.</summary>
[Trait("Speed", "Slow")]
public class CrossSexEndsLocalTests(ITestOutputHelper o)
{
    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    private static byte[]? Game(string path) => GameData.Value.GetFile(path)?.Data;

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    /// <summary>How much of a made-for limb body lands on a men's body through the maps.</summary>
    private float? Landed(string slot, string fromPath, string toPath)
    {
        string? refused = BodyRetarget.BuildPair(slot, File.ReadAllBytes(fromPath), File.ReadAllBytes(toPath), slot, false,
                                                 null, null, Maps(), out _, out var landed,
                                                 BodyRetarget.BodyMaterialReader(toPath), targetMale: true);
        if (refused != null) o.WriteLine($"  refused: {refused}");
        return landed?.VertexCoverage;
    }

    /// <summary>
    /// Step 2.1: Stormblessed's men's boots and gloves are the game's own models, packed by accident — the size set reads
    /// them as no piece of the author's, so they do not stop the women's ones from being made men's. Its women's boots
    /// and gloves are pieces.
    /// </summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed)]
    [Trait("Plan", "AC-mens-ends-crotch-3")]
    [Trait("Plan", "EDGE-mens-ends-crotch-1")]
    public void Stormblessed_packed_mens_ends_are_the_games_own() // [AC-mens-ends-crotch-3] [EDGE-mens-ends-crotch-1]
    {
        var pieces = SizeSetRefit.PiecesOf(LocalData.Path(SizeSetCrossSexLocalTests.Stormblessed), Game, includeEnds: true);
        foreach (var p in pieces) o.WriteLine($"{p.Slot} {p.GamePath} ({p.Label})");
        Assert.DoesNotContain(pieces, p => p.GamePath.Contains("/c0101e0633_", StringComparison.Ordinal)
                                           && p.Slot is "_sho" or "_glv");
        Assert.Contains(pieces, p => p.GamePath == "chara/equipment/e0633/model/c0201e0633_sho.mdl");
        Assert.Contains(pieces, p => p.GamePath == "chara/equipment/e0633/model/c0201e0633_glv.mdl");
    }

    /// <summary>
    /// Step 2.0: the share of each made-for body a shoe's legs or a glove's chest land on the men's bodies, which decides how
    /// a glove is judged. Measured 2026-10-29 on 1398: the game's women's chest lands 100.0% on TBSE's and on TBSE-X's, its
    /// legs 100.0% on TBSE's SFW and Gen A legs and 81.6% on TBSE-X's SFW legs; LavaBod+ 2.2's chest 100% on TBSE's (the
    /// size set's Stormblessed top, logged in game the same day). So a glove is judged on the whole chest, as chest pieces
    /// are, and the arm-region rule is not built; a shoe read on the game's body is refused on TBSE-X's SFW legs.
    /// </summary>
    [LocalDataFact(LocalData.GameData, CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-mens-ends-crotch-2")]
    [Trait("Plan", "EDGE-mens-ends-crotch-21")]
    public void Made_for_coverage_on_tbse() // [AC-mens-ends-crotch-2] [EDGE-mens-ends-crotch-21]
    {
        var madeFor = OtherRaceRefit.MadeForBelow(VanillaBodyCatalog.Key, null, null, 201, 101, Game)!;
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var tbseX = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.TbseX));
        string Chest(BodySizeCatalog c, string rel) => c.PathOf(c.Options.First(b => RefitCore.Normal(b.Rel) == RefitCore.Normal(rel)));
        foreach (var slot in new[] { "_top", "_dwn" })
        {
            var from = madeFor.For(slot).FirstOrDefault();
            Assert.NotNull(from);
            string fromPath = madeFor.PathOf(from!);
            foreach (var (name, path) in slot == "_top"
                         ? new[] { ("TBSE chest", Chest(tbse, UvBakeInputs.TbseChest)),
                                   ("TBSE-X chest", Path.Combine(LocalData.Path(CrossSexRefitLocalTests.TbseX),
                                                                 SupportedBodies.Of("tbse-x", "_top", false)!.Sizes[0].Rel)) }
                         : new[] { ("TBSE SFW legs", Chest(tbse, UvBakeInputs.TbseLegs[0].Rel)),
                                   ("TBSE Gen A legs", Chest(tbse, UvBakeInputs.TbseLegs[1].Rel)),
                                   ("TBSE-X SFW legs", Path.Combine(LocalData.Path(CrossSexRefitLocalTests.TbseX),
                                                                    SupportedBodies.Of("tbse-x", "_dwn", false)!.Sizes[0].Rel)) })
            {
                float? c = Landed(slot, fromPath, path);
                o.WriteLine($"game women's {slot} → {name}: {(c is { } v ? $"{v:P1}" : "refused")}");
                // The rule rests on these: the chests whole, so no glove needs judging on its arms.
                if (slot == "_top") Assert.True(c >= (float)CrossSexRefit.MinCoverage, $"{name}: {c:P1}");
            }
        }
    }
}
