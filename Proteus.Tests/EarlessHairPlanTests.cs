using System.Collections.Generic;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>Where an earless copy comes from and goes, and which of a mod's own options already draw it earless.</summary>
public class EarlessHairPlanTests
{
    private static readonly string Worn = HairPortPlan.HairPath(801, Id);

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-29")]
    public void AuthorFileFor_record_then_path_then_null()
    {
        var owner = new HairPortPlan.Owner("Cut", "Long", "Single", new Dictionary<string, string>());
        var record = new EarlessHairRecord();
        record.Swaps.Add(new EarlessHairRecord.Entry { Group = "Cut", Option = "Long", GamePath = Worn,
                                                       AuthorFile = "deep/long.mdl", File = "Proteus Earless/x.mdl" });
        // Not a copy: the file itself.
        Assert.Equal("long.mdl", EarlessHairPlan.AuthorFileFor(record, owner, Worn, "long.mdl", _ => false));
        // Recorded: what the record says.
        Assert.Equal("deep/long.mdl", EarlessHairPlan.AuthorFileFor(record, owner, Worn, "Proteus Earless/x.mdl", _ => false));
        // Unrecorded: the copy's own path, while that file is there.
        Assert.Equal("hair/a.mdl", EarlessHairPlan.AuthorFileFor(record, owner, Worn, "Proteus Earless/hair/a.mdl",
                                                                 f => f == "hair/a.mdl"));
        Assert.Null(EarlessHairPlan.AuthorFileFor(record, owner, Worn, "Proteus Earless/hair/a.mdl", _ => false));

        Assert.Equal("Proteus Earless/hair/a.mdl", EarlessHairPlan.CopyFile(@"hair\a.mdl"));
        Assert.True(EarlessHairPlan.IsEarlessFile(@"proteus earless\hair\a.mdl"));
        Assert.False(EarlessHairPlan.IsEarlessFile("Earless Hair/a.mdl"));
    }

    [Theory]
    [Trait("Plan", "EDGE-earless-miqote-hair-25")]
    [InlineData("single")]
    [InlineData("multi")]
    [InlineData("separate")]
    public void Hint_finds_earless_options_in_single_multi_and_separate_groups(string layout)
    {
        string groups = layout switch
        {
            "single" => "[" + Group("Races", "Single", ("Miqote", new() { [Worn] = "eared.mdl" }),
                                    ("Miqote Earless", new() { [Worn] = "earless.mdl" })) + "]",
            "multi" => "[" + Group("Base", "Single", ("Miqote", new() { [Worn] = "eared.mdl" })) + ","
                       + Group("Extras", "Multi", ("miqote earless", new() { [Worn] = "earless.mdl" })) + "]",
            _ => "[" + Group("Miqo'te Vanilla Ears", "Single", ("Bangs", new() { [Worn] = "eared.mdl" })) + ","
                 + Group("Miqo'te Earless", "Single", ("Bangs", new() { [Worn] = "earless.mdl" })) + "]",
        };
        using var mod = new Mod(groups);
        mod.Put("eared.mdl", EaredHairModel(801));
        mod.Put("earless.mdl", EarlessHairModel(801));
        string expected = layout switch
        {
            "single" => "Races / Miqote Earless",
            "multi" => "Extras / miqote earless",
            _ => "Miqo'te Earless / Bangs",
        };
        Assert.Equal([expected], EarlessHairPlan.EarlessOptionHint(mod.Owners, Worn, "eared.mdl", r => Read(mod, r)));
        // Never the file worn itself, and never an eared one.
        Assert.Empty(EarlessHairPlan.EarlessOptionHint(mod.Owners, Worn, "earless.mdl", r => Read(mod, r)));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-25")]
    public void Hint_never_lists_a_proteus_copy_or_a_port()
    {
        // After a conversion both options name Proteus files: neither is the mod's own earless option.
        using var mod = new Mod("[" + Group("Cut", "Single",
                                    ("Long", new() { [Worn] = "Proteus Earless/long.mdl" }),
                                    ("Port", new() { [Worn] = "Hair Ports/x/long.mdl" }),
                                    ("Bald", new() { [Worn] = "bald.mdl" }),
                                    ("Earless", new() { [Worn] = "earless.mdl" })) + "]");
        mod.Put("Proteus Earless/long.mdl", EarlessHair.Make(EaredHairModel(801), out _)!.Model);
        mod.Put("Hair Ports/x/long.mdl", EarlessHairModel(801));
        mod.Put("bald.mdl", SyntheticModel.BuildSurface([], Sphere("/mt_c0801h0130_hir_a.mtrl", Head, 0.1f, [("j_kao", 1f)],
                                                                   minY: 2f)));
        mod.Put("earless.mdl", EarlessHairModel(801));
        Assert.Equal(["Cut / Earless"], EarlessHairPlan.EarlessOptionHint(mod.Owners, Worn, "Proteus Earless/long.mdl",
                                                                          r => Read(mod, r)));
    }

    private static byte[]? Read(Mod mod, string rel) => mod.Has(rel) ? mod.Get(rel) : null;
}
