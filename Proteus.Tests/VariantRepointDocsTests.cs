using System.IO;
using Xunit;

namespace Proteus.Tests;

/// <summary>What "Gear variants" changes in an author's mod is written down where authors and agents look.</summary>
public class VariantRepointDocsTests
{
    [Fact]
    [Trait("Plan", "AC-imc-all-variants-9")]
    public void For_creators_explains_gear_variants()
    {
        var doc = File.ReadAllText(ShaderUpgradePlanTests.SourceFile("For Creators.md")).Replace("\r\n", "\n");
        var section = doc[doc.IndexOf("### Gear variants", System.StringComparison.Ordinal)..];
        section = section[..section.IndexOf("### Distributing", System.StringComparison.Ordinal)].Replace("\n", " ");
        foreach (var needed in new[] { "Proteus/variantrepoint.json", "Your material redirects are **copied**",
                                       "**No IMC entry is written.**", "your own IMC data", "no higher   mod covers",
                                       "turn it off while you work on a mod", "Undo for what I'm wearing", "Apply to All Variants" })
            Assert.Contains(needed, section);
        Assert.DoesNotContain("never reaches", section);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-9")]
    public void Agents_lists_the_writer_as_sensitive()
        => Assert.Contains("VariantRepoint/VariantRepointWriter.cs` + `PenumbraModMeta.EditContainers",
                           File.ReadAllText(ShaderUpgradePlanTests.SourceFile("AGENTS.md")));
}
