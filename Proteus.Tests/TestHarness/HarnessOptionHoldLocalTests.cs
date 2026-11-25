using System.IO;
using System.Linq;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The option hold's resolution (<see cref="HarnessOptionLookup.ResolveSelection"/>) against real and legacy manifests.
/// </summary>
public class HarnessOptionHoldLocalTests
{
    [LocalDataFact(LocalData.StarlightMod)]
    [Trait("Plan", "AC-harness-option-hold-9")]
    [Trait("Plan", "EDGE-harness-option-hold-1")]
    public void Every_single_choice_option_of_a_real_mod_resolves_to_itself()
    {
        // A published mod (Starlight, em dashes in its group names), read only: every option of every Single group a
        // hold can name resolves to itself, in the mod's own spelling.
        var groups = HarnessOptionLookup.ReadModGroups(LocalData.Path(LocalData.StarlightMod));
        Assert.NotNull(groups);
        var singles = groups!.Where(g => !g.Multi).ToList();
        Assert.NotEmpty(singles);
        foreach (var g in singles)
            foreach (var o in g.Options)
            {
                var r = HarnessOptionLookup.ResolveSelection(groups, [(g.Name, [o])]);
                Assert.True(r.Ok, $"{g.Name} / {o}: {r.Message}");
                Assert.Equal([o], r.Selection![g.Name]);
            }
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-7")]
    [Trait("Plan", "EDGE-harness-option-hold-6")]
    public void Legacy_group_files_are_read_with_their_type_and_a_broken_one_is_unreadable()
    {
        using var temp = new TempDir();
        string root = temp.Sub("mod");
        File.WriteAllText(Path.Combine(root, "meta.json"), """{"FileVersion":3}""");
        File.WriteAllText(Path.Combine(root, "group_001_size.json"),
            """{"Name":"Size","Type":"Single","Options":[{"Name":"S"},{"Name":"L"}]}""");
        File.WriteAllText(Path.Combine(root, "group_002_extras.json"),
            """{"Name":"Extras","Type":"Combining","Options":[{"Name":"Belt"}]}""");
        File.WriteAllText(Path.Combine(root, "group_003_bare.json"), """{"Name":"Bare","Options":[{"Name":"X"}]}""");
        var groups = HarnessOptionLookup.ReadModGroups(root)!;
        Assert.False(groups.Single(g => g.Name == "Size").Multi);
        Assert.True(groups.Single(g => g.Name == "Extras").Multi);
        Assert.False(groups.Single(g => g.Name == "Bare").Multi);   // no Type: Penumbra's Single
        Assert.False(HarnessOptionLookup.ResolveSelection(groups, [("Bare", ["X", "X"])]).Ok);

        File.WriteAllText(Path.Combine(root, "group_004_broken.json"), "[1,2]");
        Assert.Null(HarnessOptionLookup.ReadModGroups(root));
        Assert.Equal("mod-unreadable", HarnessOptionLookup.ResolveSelection(null, [("Size", ["S"])]).Code);
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-3")]
    [Trait("Plan", "EDGE-harness-option-hold-3")]
    public void A_case_ambiguous_group_or_option_is_refused()
    {
        HarnessOptionLookup.ModGroup[] groups =
        [
            new("Size", false, ["S", "s"]), new("size", false, ["M"]),
        ];
        Assert.Equal("invalid-field", HarnessOptionLookup.ResolveSelection(groups, [("SIZE", ["M"])]).Code);
        var sGroups = new HarnessOptionLookup.ModGroup[] { new("Size", false, ["Small", "SMALL"]) };
        Assert.Equal("invalid-field", HarnessOptionLookup.ResolveSelection(sGroups, [("Size", ["small"])]).Code);
    }
}
