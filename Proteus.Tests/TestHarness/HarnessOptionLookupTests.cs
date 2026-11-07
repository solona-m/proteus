using System;
using System.Collections.Generic;
using System.IO;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>#84: a binding route's group/option resolved against the mod's own groups.</summary>
public class HarnessOptionLookupTests
{
    private static readonly List<(string Name, IReadOnlyList<string> Options)> Groups =
    [
        ("Lace", ["High", "Low"]),
        ("Colour", ["Red", "red"]),     // two options that differ only by case
        ("Size", ["Small"]),
        ("size", ["Big"]),              // two groups that differ only by case
    ];

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-11")]
    public void Exact_wins_then_a_single_case_insensitive_match()
    {
        var exact = HarnessOptionLookup.Resolve(Groups, "Lace", "High");
        Assert.True(exact.Ok);
        Assert.Equal(("Lace", "High"), (exact.Group, exact.Option));

        var loose = HarnessOptionLookup.Resolve(Groups, "LACE", "low");
        Assert.True(loose.Ok);
        Assert.Equal(("Lace", "Low"), (loose.Group, loose.Option));

        Assert.Equal(("Size", "Small"), (HarnessOptionLookup.Resolve(Groups, "Size", "Small").Group, "Small"));
        Assert.Equal("size", HarnessOptionLookup.Resolve(Groups, "size", "Big").Group);
        Assert.Equal("red", HarnessOptionLookup.Resolve(Groups, "Colour", "red").Option);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-11")]
    public void Ambiguous_without_an_exact_match_is_400()
    {
        var g = HarnessOptionLookup.Resolve(Groups, "SIZE", "Small");
        Assert.Equal((400, "invalid-field"), (g.Status, g.Code));
        var o = HarnessOptionLookup.Resolve(Groups, "Colour", "RED");
        Assert.Equal((400, "invalid-field"), (o.Status, o.Code));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-10")]
    [Trait("Plan", "EDGE-issue-sweep-c-12")]
    public void Missing_group_option_or_no_groups_is_404_and_unreadable_is_409()
    {
        Assert.Equal((404, "option-not-found"), Code(HarnessOptionLookup.Resolve(Groups, "Nope", "High")));
        Assert.Equal((404, "option-not-found"), Code(HarnessOptionLookup.Resolve(Groups, "Lace", "Medium")));
        Assert.Equal((404, "option-not-found"), Code(HarnessOptionLookup.Resolve([], "Lace", "High")));
        Assert.Equal((409, "mod-unreadable"), Code(HarnessOptionLookup.Resolve(null, "Lace", "High")));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-39")]
    public void ReadGroups_reads_legacy_group_files_and_refuses_a_broken_one()
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus-optlookup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(HarnessOptionLookup.ReadGroups(root));   // no meta.json
            File.WriteAllText(Path.Combine(root, "meta.json"), "{\"FileVersion\":3}");
            Assert.Empty(HarnessOptionLookup.ReadGroups(root)!);
            File.WriteAllText(Path.Combine(root, "group_001_lace.json"), "{\"Name\":\"Lace\",\"Options\":[{\"Name\":\"High\"}]}");
            var groups = HarnessOptionLookup.ReadGroups(root)!;
            Assert.Equal("Lace", groups[0].Name);
            Assert.Equal(["High"], groups[0].Options);
            File.WriteAllText(Path.Combine(root, "group_002_bad.json"), "{ nope");
            Assert.Null(HarnessOptionLookup.ReadGroups(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private static (int, string?) Code(HarnessOptionLookup.Result r) => (r.Status, r.Code);
}
