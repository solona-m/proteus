using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What a config written by an older build turns into.
/// <para/>
/// Every step of <c>Configuration.Migrate</c> exists because a property default reaches only BRAND-NEW
/// configs, so a setting whose default changes has to be moved for everyone else explicitly. Getting one
/// wrong silently resets a choice somebody made, which is the failure this file is here to catch — until
/// now the migration was the one piece of code whose whole job is not losing settings and had no test.
/// </summary>
public class ConfigurationMigrationTests
{
    /// <summary>
    /// A brand-new config is stamped at the current version by its property initializer, so it must never
    /// run a step written for a setting it was never saved with. This is the contract every step below
    /// relies on being able to assume.
    /// </summary>
    [Fact]
    public void A_new_config_is_already_current_and_runs_no_step()
    {
        var config = new Configuration();
        Assert.Equal(Configuration.CurrentVersion, config.Version);

        // AutoHatCompat is the tell: the v6 step forces it false, and a new config must keep the property
        // default instead.
        bool before = config.AutoHatCompat;
        config.Migrate();
        Assert.Equal(before, config.AutoHatCompat);
    }

    /// <summary>
    /// v6 -> v7 turns the redundancy pass on for everyone, whatever the old body-specific dropdown said.
    /// <para/>
    /// Both stored values map to true, and that is the decision, not an oversight: the old setting shipped
    /// Off and named a body most users do not wear, so "never found this dropdown" and "tried it and set it
    /// back" are the same byte on disk. Reading it would switch the feature off for essentially every
    /// existing config on the strength of a choice almost none of them made.
    /// </summary>
    [Theory]
    [InlineData(ConnectorMeshMode.Off)]
    [InlineData(ConnectorMeshMode.Neolithe)]
    public void The_redundancy_pass_is_turned_on_for_every_existing_config(ConnectorMeshMode stored)
    {
        var config = new Configuration
        {
            Version = 6,
            HideConnectorMeshes = stored,
            HideRedundantMeshes = false,   // as a config written before the property existed deserializes
        };

        config.Migrate();

        Assert.True(config.HideRedundantMeshes);
        Assert.Equal(Configuration.CurrentVersion, config.Version);
    }

    /// <summary>
    /// Every step is cumulative: a config from long ago has to arrive in the same state as one from last
    /// week, not just get its version stamped.
    /// </summary>
    [Fact]
    public void A_config_from_version_one_runs_every_step()
    {
        var config = new Configuration
        {
            Version = 1,
            EnableCompression = true,
            DisableAutoRedraw = true,
            AutoHatCompat = true,
            HideRedundantMeshes = false,
        };

        config.Migrate();

        Assert.False(config.EnableCompression);        // v1 -> v2
        Assert.False(config.AutoRedraw);               // v3 -> v4, carried across from the opt-out
        Assert.False(config.AutoHatCompat);            // v6, which took back the v5 flip
        Assert.True(config.HideRedundantMeshes);       // v6 -> v7
        Assert.Equal(Configuration.CurrentVersion, config.Version);
    }

    /// <summary>
    /// Migrating twice must not undo anything. <c>Initialize</c> saves the result, so the second run sees a
    /// config already stamped current — but a step that ran on version alone would still fire if the stamp
    /// were ever written before the steps.
    /// </summary>
    [Fact]
    public void Migrating_an_already_current_config_changes_nothing()
    {
        var config = new Configuration { Version = 6, HideRedundantMeshes = false };
        config.Migrate();
        config.HideRedundantMeshes = false;   // as if the user then turned it off
        config.Migrate();

        Assert.False(config.HideRedundantMeshes);
    }
}
