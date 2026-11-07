using System;
using System.Collections.Generic;
using Penumbra.Api.Enums;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Which mod-setting events reach Gear variants and Gear shaders.</summary>
public class PlayerSettingsFilterTests
{
    private static readonly Guid Player = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-5")]
    [Trait("Plan", "EDGE-idle-disk-io-22")]
    public void OtherCollection_Dropped_PlayerPassed()
    {
        // [AC-idle-disk-io-5] [EDGE-idle-disk-io-22]
        foreach (var kind in new[] { ModSettingChange.Setting, ModSettingChange.TemporarySetting,
                                     ModSettingChange.EnableState, ModSettingChange.TemporaryMod })
        {
            Assert.False(PlayerSettingsFilter.Passes(kind, Other, Player, "Some Mod"));
            Assert.True(PlayerSettingsFilter.Passes(kind, Player, Player, "Some Mod"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-19")]
    public void ManagedMod_Dropped()
    {
        // [EDGE-idle-disk-io-19]
        Assert.False(PlayerSettingsFilter.Passes(ModSettingChange.Edited, Player, Player, SidecarDiscoveryService.ManagedModDir));
        Assert.False(PlayerSettingsFilter.Passes(ModSettingChange.Setting, Player, Player, "proteus"));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-20")]
    public void Edited_AnyCollection_Passes()
    {
        // [EDGE-idle-disk-io-20]
        Assert.True(PlayerSettingsFilter.Passes(ModSettingChange.Edited, Other, Player, "Some Mod"));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-21")]
    public void NullPlayerCollection_Passes()
    {
        // [EDGE-idle-disk-io-21]
        Assert.True(PlayerSettingsFilter.Passes(ModSettingChange.Setting, Other, null, "Some Mod"));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-5")]
    public void Instance_RaisesPassedOnlyForPassingEvents()
    {
        // [AC-idle-disk-io-5]
        int asked = 0;
        var filter = new PlayerSettingsFilter(() => { asked++; return Player; });
        var passed = new List<(ModSettingChange, string)>();
        filter.Passed += (c, d) => passed.Add((c, d));

        filter.OnModSettingChanged(ModSettingChange.TemporarySetting, Other, "Sync Pair Mod", false);
        filter.OnModSettingChanged(ModSettingChange.Setting, Player, "Worn Mod", false);
        filter.OnModSettingChanged(ModSettingChange.Edited, Other, "Edited Mod", false);
        filter.OnModSettingChanged(ModSettingChange.Setting, Player, SidecarDiscoveryService.ManagedModDir, false);

        Assert.Equal([(ModSettingChange.Setting, "Worn Mod"), (ModSettingChange.Edited, "Edited Mod")], passed);
        // The collection is asked for only when the answer matters (not for Edited, not for the managed mod).
        Assert.Equal(2, asked);
    }
}
