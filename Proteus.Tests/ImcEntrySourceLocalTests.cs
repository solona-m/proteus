using Lumina;
using Lumina.Data.Files;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Every variant of a real gear <c>.imc</c>, read the way "Gear variants" reads it (<see cref="ImcEntrySource.GameEntries"/>),
/// checked against Lumina's own IMC parser — an independent reading of the layout, not our offsets checked by themselves.
/// </summary>
public class ImcEntrySourceLocalTests
{
    [LocalDataFact(LocalData.GameData)]
    [Trait("Speed", "Slow")]
    [Trait("Plan", "EDGE-imc-all-variants-13")]
    public void Real_equipment_and_accessory_imc_offsets()
    {
        var game = new GameData(LocalData.Path(LocalData.GameData));
        byte[]? Read(string p) => game.GetFile(p)?.Data;

        foreach (var (tag, label, part) in new[] { ("e6255", "Body", 1), ("a0053", "Right ring", 3), ("a0053", "Left ring", 4) })
        {
            var lumina = game.GetFile<ImcFile>(ImcEntrySource.ImcPathForSet(tag)!)!;
            var count = ImcEntrySource.VariantCount(Read, tag);
            Assert.Equal(lumina.Count, count);
            var entries = ImcEntrySource.GameEntries(Read, tag, label)!;
            Assert.Equal(count + 1, entries.Length);
            Same(lumina.GetDefaultVariant(part), entries[0]);   // the default set
            for (int v = 1; v <= count; v++)
                Same(lumina.GetVariant(part, v - 1), entries[v]);
            Assert.True(entries[1].MaterialId >= 1);
        }

        static void Same(ImcFile.ImageChangeData expected, ImcEntry actual)
        {
            Assert.Equal(expected.MaterialId, actual.MaterialId);
            Assert.Equal(expected.DecalId, actual.DecalId);
            Assert.Equal(expected.VfxId, actual.VfxId);
            Assert.Equal(expected.AttributeMask, actual.AttributeMask);
            Assert.Equal(expected.SoundId >> 10, actual.SoundId);
        }
    }
}
