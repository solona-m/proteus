using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What the Toggles tab calls each model in its picker. The mod's own option label leads, because that is
/// what the user is choosing between — these are almost always sizes, and the slot is the same on every row.
/// </summary>
public class PartsPanelLabelTests
{
    private static PenumbraModMeta.Redirect R(string gamePath, string file, string source)
        => new(gamePath, file, source);

    private const string Legs = "chara/equipment/e0488/model/c0201e0488_dwn.mdl";
    private const string Top = "chara/equipment/e0488/model/c0201e0488_top.mdl";

    /// <summary>One garment in three sizes: the size is the whole label, with nothing in front of it.</summary>
    [Fact]
    public void SizesReadAsThemselves()
    {
        var labels = PartsPanel.ModelLabels([
            R(Legs, "small/dwn.mdl", "Pant Size / Small"),
            R(Legs, "medium/dwn.mdl", "Pant Size / Medium"),
            R(Legs, "large/dwn.mdl", "Pant Size / Large"),
        ]);

        Assert.Equal(["Pant Size / Small", "Pant Size / Medium", "Pant Size / Large"], labels);
    }

    /// <summary>A model the mod publishes unconditionally has no option name to show, so it falls back.</summary>
    [Fact]
    public void AModelWithNoOptionFallsBackToItsSlot()
        => Assert.Equal(["Legs — e0488"], PartsPanel.ModelLabels([R(Legs, "dwn.mdl", "")]));

    /// <summary>
    /// One option supplying two garments would otherwise give two identical rows — the slot is appended to
    /// exactly those, and to nothing else.
    /// </summary>
    [Fact]
    public void ACollidingOptionNameGainsItsSlot()
    {
        var labels = PartsPanel.ModelLabels([
            R(Legs, "small/dwn.mdl", "Sizes / Small"),
            R(Top, "small/top.mdl", "Sizes / Small"),
            R(Legs, "large/dwn.mdl", "Sizes / Large"),
        ]);

        Assert.Equal("Sizes / Small  (Legs — e0488)", labels[0]);
        Assert.Equal("Sizes / Small  (Body — e0488)", labels[1]);
        // Untouched: it never collided with anything.
        Assert.Equal("Sizes / Large", labels[2]);
    }

    [Fact]
    public void APathThatNamesNoSlotFallsBackToItsFileName()
        => Assert.Equal(["thing.mdl"], PartsPanel.ModelLabels([R("chara/thing.mdl", "thing.mdl", "")]));
}
