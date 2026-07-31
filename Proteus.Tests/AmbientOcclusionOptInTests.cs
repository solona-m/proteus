using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// AO / Skindenting is opt-IN: a pack must ask for it, and a user choice overrides whatever the pack said.
/// These pin the resolution order in <see cref="Configuration.AmbientOcclusionEnabledFor"/>.
/// </summary>
public class AmbientOcclusionOptInTests
{
    [Fact]
    public void NobodySaidAnything_IsOff()
    {
        var c = new Configuration();
        Assert.False(c.AmbientOcclusionEnabledFor("SomeMod", null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ThePackDecidesWhenTheUserHasNoOpinion(bool declared, bool expected)
    {
        var c = new Configuration();
        Assert.Equal(expected, c.AmbientOcclusionEnabledFor("SomeMod", declared));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AUserOptInWinsOverThePack(bool? declared, bool expected)
    {
        var c = new Configuration();
        c.AmbientOcclusionOverrides["SomeMod"] = true;
        Assert.Equal(expected, c.AmbientOcclusionEnabledFor("SomeMod", declared));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void AUserOptOutWinsOverThePack(bool? declared, bool expected)
    {
        var c = new Configuration();
        c.AmbientOcclusionOverrides["SomeMod"] = false;
        Assert.Equal(expected, c.AmbientOcclusionEnabledFor("SomeMod", declared));
    }

    [Fact]
    public void ALegacyOptOutStillSuppressesAPackThatAsks()
    {
        // Configs written under the old "on unless opted out" rule must keep working: a mod the user had
        // switched off stays off even though the pack declares it.
        var c = new Configuration();
        c.AmbientOcclusionDisabledMods.Add("SomeMod");
        Assert.False(c.AmbientOcclusionEnabledFor("SomeMod", true));
    }

    [Fact]
    public void ANewOverrideBeatsALegacyOptOut()
    {
        var c = new Configuration();
        c.AmbientOcclusionDisabledMods.Add("SomeMod");
        c.AmbientOcclusionOverrides["SomeMod"] = true;
        Assert.True(c.AmbientOcclusionEnabledFor("SomeMod", null));
    }

    [Fact]
    public void ModDirectoriesAreComparedCaseInsensitively()
    {
        // Mod directories are compared OrdinalIgnoreCase everywhere else; both collections must match.
        var c = new Configuration();
        c.AmbientOcclusionOverrides["SomeMod"] = true;
        Assert.True(c.AmbientOcclusionEnabledFor("somemod", null));

        var d = new Configuration();
        d.AmbientOcclusionDisabledMods.Add("SomeMod");
        Assert.False(d.AmbientOcclusionEnabledFor("SOMEMOD", true));
    }
}
