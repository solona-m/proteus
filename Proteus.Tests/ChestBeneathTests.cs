using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The chest beneath a legs garment's waist (see <see cref="RefitCore.ChestBeneath"/>): which chest, and when a
/// legs refit made before is made again.</summary>
public class ChestBeneathTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Legs = "chara/equipment/e6010/model/c0201e6010_dwn.mdl";
    private const string Top = "chara/equipment/e6010/model/c0201e6010_top.mdl";
    private static readonly byte[] Model = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0)));

    private static BodyOption Chest(string name, string section = "")
        => new("Chest", section, name, name + ".mdl", "chara/equipment/e0000/model/c0201e0000_top.mdl", "_top");

    private static BodyOption LegsOption(string name, string section = "")
        => new("Legs", section, name, name + ".mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-18")]
    public void Beneath_never_falls_to_the_first_chest()
    {
        // Rue's legs sizes name no chest: Companion's fall-through would take the smallest chest, Beneath takes none.
        List<BodyOption> chests = [Chest("Small", "None"), Chest("Medium", "None"), Chest("Large", "None")];
        Assert.Null(RefitCore.Beneath(chests, LegsOption("Watermelon Crushers - A")));
        Assert.Equal("Small", AutoRefitDecisions.Companion(chests, LegsOption("Watermelon Crushers - A"), null)!.Name);
        // Named for it: taken.
        Assert.Equal("Medium", RefitCore.Beneath(chests, LegsOption("Medium", "None"))!.Name);
        Assert.Equal("Medium", RefitCore.Beneath([Chest("Medium", "Gen A")], LegsOption("Medium", "Other"))!.Name);
    }

    private static string NewMod()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-beneath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Geometric", "Someone", ""));
        return root;
    }

    private static void Save(string root, string gamePath, Dictionary<string, string>? targets)
        => Assert.True(BodyRetargetWriter.Save(root, "Size", gamePath, "rue", "Bibo+ — Type A Small",
                                               [new BodyRetargetWriter.Refit("Rue WC A", Model, "WC A")], auto: true,
                                               provenance: targets == null ? null
                                                   : new BodyRetargetWriter.Provenance("fix pubes", "legs.mdl", targets)).Ok);

    [Fact]
    [Trait("Plan", "AC-outfit-fit-8")]
    [Trait("Plan", "EDGE-outfit-fit-20")]
    public void A_legs_refit_onto_another_chest_is_made_again_rather_than_put_back()
    {
        string root = NewMod();
        try
        {
            Save(root, Legs, new() { ["_dwn"] = "wca.mdl", [RefitCore.ChestBeneathKey] = "small.mdl" });
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Legs, chestBeneath: "small.mdl"));
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Legs, chestBeneath: "SMALL.mdl"));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Legs, chestBeneath: "medium.mdl"));
            // No chest goes along now (below the gate, or none chosen): as good as any.
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Legs));

            // Made before the chest went along: put back as it was, unless asked for what is worn now.
            Save(root, Legs, new() { ["_dwn"] = "wca.mdl" });
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Legs, chestBeneath: "medium.mdl"));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Legs, forced: true, chestBeneath: "medium.mdl"));
            Save(root, Legs, null);
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Legs, chestBeneath: "medium.mdl"));

            // A chest piece's own entry is never asked about a chest beneath.
            Save(root, Top, new() { ["_top"] = "small.mdl", ["_dwn"] = "wca.mdl", [RefitCore.ChestBeneathKey] = "small.mdl" });
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue WC A", Top, chestBeneath: "medium.mdl"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-outfit-fit-5")]
    public void The_chest_beneath_is_recorded_under_its_own_key()
    {
        var targets = AutoRefitWatcher.TargetsOf("_dwn", LegsOption("Watermelon Crushers - A"), [], Chest("Medium", "None"));
        Assert.Equal("Medium.mdl", targets[RefitCore.ChestBeneathKey]);
        Assert.Equal("Watermelon Crushers - A.mdl", targets["_dwn"]);
        Assert.False(targets.ContainsKey("_top"));
        Assert.False(AutoRefitWatcher.TargetsOf("_dwn", LegsOption("A"), []).ContainsKey(RefitCore.ChestBeneathKey));
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-21")]
    public void Options_join_as_before_whatever_chest_went_beneath_a_skirt()
    {
        // A skirt's refit recorded with the chest beneath it, in a group the top and skirt share.
        var record = new BodyRetargetWriter.Record
        {
            Group = "Size",
            Options =
            [
                new BodyRetargetWriter.Entry
                {
                    Group = "Size", Name = "Rue WC A", GamePath = Legs, BodyMod = "rue", Auto = true,
                    Targets = new() { ["_dwn"] = "wca.mdl", [RefitCore.ChestBeneathKey] = "medium.mdl" },
                },
            ],
        };
        var chosen = new Dictionary<string, string> { ["_top"] = "medium.mdl", ["_dwn"] = "wca.mdl" };
        // A top refitted later makes its own option: it shares no part with the skirt but the chest beneath.
        Assert.Equal("Rue M", RefitCore.JoinOption(record, "Size", Top, "rue", chosen,
                                                   new Dictionary<string, string> { ["_top"] = "medium.mdl" }, "Rue M"));
        // A shoe on the same legs still joins the skirt's option, as it did before the chest was recorded.
        const string shoe = "chara/equipment/e6010/model/c0201e6010_sho.mdl";
        Assert.Equal("Rue WC A", RefitCore.JoinOption(record, "Size", shoe, "rue", chosen,
                                                      new Dictionary<string, string> { ["_dwn"] = "wca.mdl" }, "Rue Shoe"));
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-15")]
    [Trait("Plan", "EDGE-outfit-fit-16")]
    public void Only_a_legs_garment_whose_skin_did_not_read_the_chest_asks_for_the_chest_beneath_a_chosen_chest()
    {
        var chest = Chest("Medium");
        Assert.True(RefitCore.WantsChestBeneath("_dwn", [], chest));
        Assert.True(RefitCore.WantsChestBeneath("_dwn", ["_sho"], chest));
        // Its skin read the chest: that goes in whole, as before.
        Assert.False(RefitCore.WantsChestBeneath("_dwn", ["_top"], chest));
        // No chest chosen: never one that only follows the legs.
        Assert.False(RefitCore.WantsChestBeneath("_dwn", [], null));
        Assert.False(RefitCore.WantsChestBeneath("_top", [], chest));
    }
}
