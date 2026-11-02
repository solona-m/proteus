using System;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Every installed body, read for physics-variant chest groups (#59): only NIRÆ has one that is split off. AB Body's
/// "SFW Smallclothes Top Toggle" matches the variant rule too, but AB Body chooses no chest by <c>Atr</c>, so its sizes
/// stay as they were.
/// </summary>
public class NiraeSquishBodiesLocalTests(ITestOutputHelper output)
{
    private const string AbBody = "[Cry] AB Body [Body][DT] v2.0.9";

    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "AC-59-nirae-squish-4")]
    [Trait("Plan", "EDGE-59-nirae-squish-19")]
    [Trait("Speed", "Slow")]
    public void Installed_bodies_have_no_variants_but_nirae()
    {
        var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
        foreach (var b in bodies)
            output.WriteLine($"{b.Dir}: {b.Catalog.For("_top").Count} chest size(s), {b.Catalog.Variants.Count} variant(s)");
        Assert.True(bodies.Count >= 8, $"only {bodies.Count} bodies installed — this checks too little");

        foreach (var b in bodies.Where(b => b.Dir != "NIRÆ body"))
        {
            Assert.Empty(b.Catalog.Variants);
            Assert.DoesNotContain(b.Catalog.For("_top"), BodySizeCatalog.IsShape);
        }
        Assert.Equal(4, Assert.Single(bodies, b => b.Dir == "NIRÆ body").Catalog.Variants.Count);

        var ab = Assert.Single(bodies, b => b.Dir == AbBody).Catalog.For("_top", "0201");
        foreach (var name in new[] { "SFW XS", "SFW S", "SFW M", "SFW L" })
            Assert.False(BodySizeCatalog.IsShape(Assert.Single(ab, o => o.Group == "SFW Smallclothes Top Toggle" && o.Name == name)));
    }
}
