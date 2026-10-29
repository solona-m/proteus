using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Every playable race has a vanilla body to refit its gear from, in all four slots — though most ship none of their
/// own and the game draws a Midlander's, a Roegadyn man's or a Lalafell man's.
/// </summary>
[Trait("Speed", "Slow")]
public class VanillaBodyRaceLocalTests
{
    [LocalDataTheory(LocalData.GameData)]
    [InlineData("0101")] [InlineData("0201")] [InlineData("0301")] [InlineData("0401")]
    [InlineData("0501")] [InlineData("0601")] [InlineData("0701")] [InlineData("0801")]
    [InlineData("0901")] [InlineData("1001")] [InlineData("1101")] [InlineData("1201")]
    [InlineData("1301")] [InlineData("1401")] [InlineData("1501")] [InlineData("1601")]
    [InlineData("1701")] [InlineData("1801")]
    public void EveryRaceHasAVanillaBodyInEverySlot(string race)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var catalog = VanillaBodyCatalog.Read(race, p => data.GetFile(p)?.Data, drawn: true);

        foreach (var slot in new[] { "_top", "_dwn", "_glv", "_sho" })
            Assert.Single(catalog.For(slot, race));
    }
}
