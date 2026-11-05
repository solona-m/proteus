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
        var doc = File.ReadAllText(ShaderUpgradePlanTests.SourceFile("For Creators.md"));
        foreach (var needed in new[] { "### Gear variants", "Proteus/variantrepoint.json", "default data",
                                       "Undo for what I'm wearing", "Apply to All Variants", "everyone wearing those variants" })
            Assert.Contains(needed, doc);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-9")]
    public void Agents_lists_the_writer_as_sensitive()
        => Assert.Contains("VariantRepoint/VariantRepointWriter.cs",
                           File.ReadAllText(ShaderUpgradePlanTests.SourceFile("AGENTS.md")));
}
