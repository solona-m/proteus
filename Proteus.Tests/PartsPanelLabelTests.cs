using System.Linq;
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

    private const string RingMidlander = "chara/accessory/a0058/model/c0101a0058_rir.mdl";
    private const string RingHrothgar = "chara/accessory/a0058/model/c1501a0058_rir.mdl";

    /// <summary>
    /// "DT Coeurl Sideburns": every option ships the vanilla ring at c0101 and the sideburns at c1501. The option and
    /// the slot are the same for both, so only the race tells the ring from the sideburns.
    /// </summary>
    [Fact]
    public void OneItemPerRaceGainsTheRace()
    {
        var labels = PartsPanel.ModelLabels([
            R(RingMidlander, "common/3/ring.mdl", "Face Selection / Face 1"),
            R(RingHrothgar, "face 1/sideburns.mdl", "Face Selection / Face 1"),
            R(RingHrothgar, "face 2/sideburns.mdl", "Face Selection / Face 2"),
        ]);

        Assert.Equal("Face Selection / Face 1  (Right ring — a0058, Midlander M)", labels[0]);
        Assert.Equal("Face Selection / Face 1  (Right ring — a0058, Hrothgar M)", labels[1]);
        Assert.Equal("Face Selection / Face 2", labels[2]);
    }

    /// <summary>With no option name the label is already the slot, so the race is all that is added.</summary>
    [Fact]
    public void AnUnconditionalModelPerRaceGainsOnlyTheRace()
    {
        var labels = PartsPanel.ModelLabels([R(RingMidlander, "ring.mdl", ""), R(RingHrothgar, "sideburns.mdl", "")]);

        Assert.Equal(["Right ring — a0058  (Midlander M)", "Right ring — a0058  (Hrothgar M)"], labels);
    }

    /// <summary>
    /// Each option's ring and sideburns sit together, in the author's order. Sorted by the whole path, every ring came
    /// before every sideburns.
    /// </summary>
    [Fact]
    public void AnItemsRacesAreListedTogether()
    {
        var listed = PartsPanel.PublishedModels([
            R(RingMidlander, "ring.mdl", "Face 1"),
            R(RingHrothgar, "face1.mdl", "Face 1"),
            R(RingMidlander, "ring.mdl", "Face 2"),
            R(RingHrothgar, "face2.mdl", "Face 2"),
            R("chara/accessory/a0058/texture/x.tex", "x.tex", "Face 2"),
        ]);

        Assert.Equal(["ring.mdl", "face1.mdl", "ring.mdl", "face2.mdl"], listed.Select(r => r.File));
    }

    [Fact]
    public void APathThatNamesNoSlotFallsBackToItsFileName()
        => Assert.Equal(["thing.mdl"], PartsPanel.ModelLabels([R("chara/thing.mdl", "thing.mdl", "")]));

    /// <summary>
    /// Body size's "Open the Highlander M model": the row of the author's own model for that race — the one in the open
    /// model's option when several options ship one — or none, and then no button.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-6")]
    [Trait("Plan", "EDGE-refit-other-race-17")]
    public void TheSiblingModelIsTheOneInTheOpenModelsOption()
    {
        const string mid = "chara/equipment/e6091/model/c0101e6091_top.mdl";
        const string high = "chara/equipment/e6091/model/c0301e6091_top.mdl";
        var models = new[]
        {
            R(mid, "a/mid.mdl", "Shirt / Everything"),
            R(high, "b/high.mdl", "Shirt / Neolithe"),
            R(mid, "b/mid.mdl", "Shirt / Neolithe"),
            R(high, "a/high.mdl", "Shirt / Everything"),
        };
        Assert.Equal(3, PartsPanel.SiblingModelIndex(models, models[0], high));
        Assert.Equal(1, PartsPanel.SiblingModelIndex(models, models[2], high));
        Assert.Equal(1, PartsPanel.SiblingModelIndex(models, null, high));
        Assert.Equal(-1, PartsPanel.SiblingModelIndex(models, models[0], "chara/equipment/e6091/model/c0901e6091_top.mdl"));
    }
}
