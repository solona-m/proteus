using Newtonsoft.Json;
using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The settings text the background writer puts in Proteus.json must be what Dalamud's own SavePluginConfig writes
/// (its <c>SerializeConfig</c> is internal) and must load back the way Dalamud's <c>LoadForType</c> reads it.
/// </summary>
public class ConfigJsonTests
{
    private static Configuration Populated()
    {
        var c = new Configuration();
        c.Migrate();
        c.AutoRedraw = !c.AutoRedraw;
        return c;
    }

    // [AC-stutter-fixes-8]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-8")]
    public void MatchesDalamudSettings()
    {
        var json = ConfigJson.Serialize(Populated());
        Assert.Contains("\n", json);                                     // indented
        Assert.Contains("\"$type\": \"Proteus.Configuration, Proteus\"", json);  // objects typed, simple assembly name
        Assert.Equal(TypeNameHandling.Objects, ConfigJson.Settings.TypeNameHandling);
        Assert.Equal(TypeNameAssemblyFormatHandling.Simple, ConfigJson.Settings.TypeNameAssemblyFormatHandling);
    }

    // [EDGE-stutter-fixes-30]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-30")]
    public void RoundTripsUnderLoadForTypeAndTypedLoad()
    {
        var json = ConfigJson.Serialize(Populated());

        // LoadForType: DeserializeObject<T> with no type handling.
        var plain = JsonConvert.DeserializeObject<Configuration>(json);
        Assert.NotNull(plain);
        Assert.Equal(json, ConfigJson.Serialize(plain!));

        // The typed load.
        var typed = JsonConvert.DeserializeObject<Configuration>(json, ConfigJson.Settings);
        Assert.NotNull(typed);
        Assert.Equal(json, ConfigJson.Serialize(typed!));
    }
}
