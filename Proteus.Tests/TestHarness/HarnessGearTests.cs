using Newtonsoft.Json.Linq;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class HarnessGearTests
{
    // [AC-harness-equip-2]
    [Fact]
    public void CustomId_MatchesGlamourerLayout()
    {
        Assert.Equal(283682589901117UL, HarnessGear.CustomId("Body", 317, 2));
        Assert.Equal(0x102020000013DUL, HarnessGear.CustomId("Body", 317, 2));
        Assert.Equal(0x1020100000032UL, HarnessGear.CustomId("Body", 50, 1));
        Assert.Equal(0x1090100000001UL, HarnessGear.CustomId("LFinger", 1, 1));
        byte[] types = [1, 2, 3, 4, 5, 6, 7, 8, 9, 9];
        for (int i = 0; i < HarnessGear.Slots.Length; i++)
            Assert.Equal(types[i], (byte)((HarnessGear.CustomId(HarnessGear.Slots[i], 1, 0) >> 40) & 0xFF));
    }

    // [EDGE-harness-equip-6]
    [Fact]
    public void Rings_BothUseFingerType()
    {
        Assert.Equal(9, HarnessGear.EquipType("RFinger"));
        Assert.Equal(HarnessGear.EquipType("RFinger"), HarnessGear.EquipType("LFinger"));
        Assert.Equal(HarnessGear.CustomId("RFinger", 5, 3), HarnessGear.CustomId("LFinger", 5, 3));
    }

    // [AC-harness-equip-9]
    [Fact]
    public void TryDecode_CustomOnly()
    {
        Assert.Null(HarnessGear.TryDecode(12345));
        Assert.Null(HarnessGear.TryDecode(HarnessGear.CustomId("Body", 317, 2) | HarnessGear.BonusFlag));
        Assert.Null(HarnessGear.TryDecode(4294967164));
        Assert.Equal((317, 2), HarnessGear.TryDecode(HarnessGear.CustomId("Body", 317, 2)));
        Assert.Equal((65535, 255), HarnessGear.TryDecode(HarnessGear.CustomId("Neck", 65535, 255)));
    }

    // [EDGE-harness-equip-1]
    [Theory]
    [InlineData("body", "Body")]
    [InlineData(" RFINGER ", "RFinger")]
    [InlineData("Glasses", null)]
    [InlineData("MainHand", null)]
    [InlineData("OffHand", null)]
    [InlineData("", null)]
    [InlineData("3", null)]
    [InlineData(null, null)]
    public void Canonical_CaseInsensitive_ExcludesBonusAndWeapons(string? given, string? expected)
        => Assert.Equal(expected, HarnessGear.Canonical(given));

    [Fact]
    public void Parse_ReadsGlamourerEquipment_SkipsUnreadable()
    {
        var state = JObject.Parse("""
            {"Equipment":{"Body":{"ItemId":40000,"Stain":3,"Stain2":7},"Head":{"ItemId":"x"},"Legs":{"Stain":1}}}
            """);
        var gear = HarnessGear.Parse(state)!;
        Assert.Equal(new Worn(40000, 3, 7), gear["Body"]);
        Assert.False(gear.ContainsKey("Head"));
        Assert.False(gear.ContainsKey("Legs"));
        Assert.Null(HarnessGear.Parse(new JObject()));
        Assert.Null(HarnessGear.Parse(null));
    }
}
