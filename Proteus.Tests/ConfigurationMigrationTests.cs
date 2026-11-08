using System.Linq;
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

        // AutoHatCompat is the tell: the v8 step forces it true, and a new config must keep whatever the
        // user set instead.
        config.AutoHatCompat = false;
        config.Migrate();
        Assert.False(config.AutoHatCompat);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-10")]
    public void A_new_config_runs_no_body_memory_step()   // [EDGE-gh33-36-37-body-size-10]
    {
        var config = new Configuration();
        Assert.Equal(9, config.Version);
        config.RetargetTargets["_top"] = "chest/L.mdl";   // by hand: only a v8 config is moved
        config.Migrate();
        Assert.Single(config.RetargetTargets);
        Assert.Empty(config.RetargetSizesByBody);
    }

    // ── v8 -> v9: the Body size tool's memory per part and per body ─────────────────────────────────────

    private static Configuration Load(string json)
    {
        var settings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Objects };
        return Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json, settings)!;
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-8")]
    public void V8_config_keeps_its_body_and_sizes_per_body()   // [AC-gh33-36-37-body-size-8]
    {
        var c = Load("""{"Version":8,"RetargetBodyDir":"Neolithe","RetargetTargets":{"_top":"chest/L.mdl","_dwn":"legs/C.mdl","_glv":"hands/S.mdl"}}""");
        c.Migrate();

        var sizes = Proteus.Services.RememberedTargets.SizesOf(c, "neolithe")!;
        Assert.Equal("chest/L.mdl", sizes["_top"]);
        Assert.Equal("legs/C.mdl", sizes["_dwn"]);
        Assert.Equal("hands/S.mdl", sizes["_glv"]);
        Assert.Equal(new[] { "_dwn", "_glv", "_top" }, c.RetargetBodyByPart.Keys.Order());
        Assert.All(c.RetargetBodyByPart.Values, v => Assert.Equal("Neolithe", v));
        Assert.Empty(c.RetargetTargets);
        Assert.Equal("Neolithe", c.RetargetBodyDir);
        Assert.Equal(9, c.Version);
        Assert.Equal("Neolithe", Proteus.Services.RememberedTargets.BodyToOpen(c, "_dwn", null, ["Neolithe"], _ => true).Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-9")]
    public void A_downgrade_to_1406_and_back_keeps_what_1406_wrote()   // [EDGE-gh33-36-37-body-size-9]
    {
        // 1406 knows neither new field: it drops them on its next save and stamps the config 8.
        // (a) Nothing changed in 1406.
        var a = Load("""{"Version":8,"RetargetBodyDir":"A","RetargetTargets":{}}""");
        a.Migrate();
        Assert.Equal(9, a.Version);
        Assert.Empty(a.RetargetTargets);
        Assert.Null(Proteus.Services.RememberedTargets.SizesOf(a, "A"));
        Assert.Equal("A", Proteus.Services.RememberedTargets.BodyToOpen(a, "_top", null, ["A", "B"], _ => true).Dir);

        // (b) In 1406 the user picked B and a legs size.
        var b = Load("""{"Version":8,"RetargetBodyDir":"B","RetargetTargets":{"_dwn":"legs/M.mdl"}}""");
        b.Migrate();
        Assert.Empty(b.RetargetTargets);
        Assert.Equal(["_dwn"], Proteus.Services.RememberedTargets.SizesOf(b, "B")!.Keys);
        Assert.Equal("B", b.RetargetBodyByPart["_dwn"]);
        Assert.Single(b.RetargetBodyByPart);

        // Never over what is there: a stale old-style size does not replace the per-body one, nor reseat a part.
        var guard = Load("""
            {"Version":8,"RetargetBodyDir":"B","RetargetTargets":{"_dwn":"old.mdl"},
             "RetargetSizesByBody":{"B":{"_dwn":"new.mdl"}},"RetargetBodyByPart":{"_dwn":"Rue"}}
            """);
        guard.Migrate();
        Assert.Equal("new.mdl", Proteus.Services.RememberedTargets.SizesOf(guard, "B")!["_dwn"]);
        Assert.Equal("Rue", guard.RetargetBodyByPart["_dwn"]);
        Assert.Empty(guard.RetargetTargets);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-9")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-22")]
    public void V9_step_fills_a_null_entry_and_never_reseats_a_part()   // [EDGE-gh33-36-37-body-size-9] [EDGE-gh33-36-37-body-size-22]
    {
        var c = Load("""
            {"Version":8,"RetargetBodyDir":"A","RetargetTargets":{"_top":"t","_dwn":"d"},
             "RetargetBodyByPart":{"_dwn":"Rue"},"RetargetSizesByBody":{"a":null}}
            """);
        c.Migrate();
        Assert.Equal(["a"], c.RetargetSizesByBody.Keys);                    // the existing key, whatever its case
        Assert.Equal("t", c.RetargetSizesByBody["a"]["_top"]);
        Assert.Equal("d", c.RetargetSizesByBody["a"]["_dwn"]);
        Assert.Equal("A", c.RetargetBodyByPart["_top"]);
        Assert.Equal("Rue", c.RetargetBodyByPart["_dwn"]);                  // a part already on a body keeps it
        Assert.Empty(c.RetargetTargets);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-11")]
    public void V8_sizes_without_a_body_are_dropped()   // [EDGE-gh33-36-37-body-size-11]
    {
        foreach (string body in new[] { "", "\"RetargetBodyDir\":\"\"," })
        {
            var c = Load("{\"Version\":8," + body + "\"RetargetTargets\":{\"_top\":\"chest/L.mdl\"}}");
            c.Migrate();
            Assert.Empty(c.RetargetSizesByBody);
            Assert.Empty(c.RetargetBodyByPart);
            Assert.Empty(c.RetargetTargets);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-12")]
    public void V8_body_without_sizes_seeds_no_part()   // [EDGE-gh33-36-37-body-size-12]
    {
        var c = Load("""{"Version":8,"RetargetBodyDir":"A"}""");
        c.Migrate();
        Assert.Empty(c.RetargetBodyByPart);
        Assert.Equal("A", Proteus.Services.RememberedTargets.BodyToOpen(c, "_top", null, ["A"], _ => true).Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-22")]
    public void Null_memory_fields_read_as_empty()   // [EDGE-gh33-36-37-body-size-22]
    {
        var c = Load("""{"Version":9,"RetargetBodyDir":"","RetargetBodyByPart":null,"RetargetSizesByBody":null}""");
        Assert.Null(Proteus.Services.RememberedTargets.SizesOf(c, "A"));
        Assert.Empty(Proteus.Services.RememberedTargets.Fillable(c, "A", _ => true, _ => false));
        Assert.Null(Proteus.Services.RememberedTargets.BodyToOpen(c, "_top", null, ["A"], _ => true).Dir);
        Proteus.Services.RememberedTargets.ChoseSize(c, "_top", "A", "_top", "x");
        Assert.Equal("x", Proteus.Services.RememberedTargets.SizesOf(c, "A")!["_top"]);
        Assert.Equal("A", c.RetargetBodyByPart["_top"]);

        var inner = Load("""{"Version":9,"RetargetSizesByBody":{"A":null}}""");
        Assert.Null(Proteus.Services.RememberedTargets.SizesOf(inner, "A"));
        Assert.Empty(Proteus.Services.RememberedTargets.Fillable(inner, "A", _ => true, _ => false));
        Proteus.Services.RememberedTargets.ChoseSize(inner, "_top", "A", "_top", "x");
        Assert.Equal("x", Proteus.Services.RememberedTargets.SizesOf(inner, "A")!["_top"]);

        // Clearing a size on a null entry forgets the entry.
        var cleared = Load("""{"Version":9,"RetargetSizesByBody":{"A":null}}""");
        Proteus.Services.RememberedTargets.ChoseSize(cleared, "_top", "A", "_top", null);
        Assert.Empty(cleared.RetargetSizesByBody);

        // Blank folders are absent: nothing is written for them.
        var blank = new Configuration();
        string before = Newtonsoft.Json.JsonConvert.SerializeObject(blank);
        Proteus.Services.RememberedTargets.PickedBody(blank, "_top", " ");
        Proteus.Services.RememberedTargets.ChoseSize(blank, "_top", "", "_top", "x");
        Assert.Equal(before, Newtonsoft.Json.JsonConvert.SerializeObject(blank));

        // A blank part body falls back to the last body.
        var part = Load("""{"Version":9,"RetargetBodyDir":"A","RetargetBodyByPart":{"_top":""}}""");
        Assert.Equal("A", Proteus.Services.RememberedTargets.BodyToOpen(part, "_top", null, ["A"], _ => true).Dir);

        var old = Load("""{"Version":8,"RetargetBodyDir":"A","RetargetTargets":{"_top":"t"},"RetargetBodyByPart":null,"RetargetSizesByBody":null}""");
        old.Migrate();
        Assert.Equal("t", Proteus.Services.RememberedTargets.SizesOf(old, "A")!["_top"]);
        var nulls = Load("""{"Version":8,"RetargetTargets":null}""");
        nulls.Migrate();
        Assert.Empty(nulls.RetargetTargets);
    }

    /// <summary>
    /// "Include shoes and gloves" in the size set is a new setting, not a changed default: a config saved
    /// before it existed loads with it on, and that needs no migration step.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-26")]
    public void SizeSetEnds_defaults_on_when_missing()   // [EDGE-keep-ends-studio-26]
    {
        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$"""{"Version":{{Configuration.CurrentVersion}},"SizeSetYas":true}""")!;
        Assert.True(config.SizeSetEnds);
        config.Migrate();
        Assert.True(config.SizeSetEnds);
        Assert.Equal(Configuration.CurrentVersion, config.Version);

        var off = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$"""{"Version":{{Configuration.CurrentVersion}},"SizeSetEnds":false}""")!;
        Assert.False(off.SizeSetEnds);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-1")]
    public void AutoUpgradeLegacyShaders_defaults_off_and_missing_property_reads_off()
    {
        Assert.False(new Configuration().AutoUpgradeLegacyShaders);
        Assert.Equal(9, Configuration.CurrentVersion);

        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("""{"Version":9,"AutoHatCompat":true}""")!;
        Assert.False(old.AutoUpgradeLegacyShaders);
        old.Migrate();
        Assert.False(old.AutoUpgradeLegacyShaders);

        var on = new Configuration { AutoUpgradeLegacyShaders = true };
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(Newtonsoft.Json.JsonConvert.SerializeObject(on))!;
        Assert.True(back.AutoUpgradeLegacyShaders);
        back.Migrate();
        Assert.True(back.AutoUpgradeLegacyShaders);
        Assert.Equal(Configuration.CurrentVersion, back.Version);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-1")]
    public void Earless_defaults_off_and_missing_property_reads_off()
    {
        var fresh = new Configuration();
        Assert.False(fresh.EarlessMiqoteHair);
        Assert.False(fresh.EarlessRestorePending);
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$"""{"Version":{{Configuration.CurrentVersion}},"AutoHairPort":true}""")!;
        old.Migrate();
        Assert.False(old.EarlessMiqoteHair);
        Assert.False(old.EarlessRestorePending);
        Assert.Equal(Configuration.CurrentVersion, old.Version);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-1")]
    public void Earless_round_trips()
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new Configuration { EarlessMiqoteHair = true, EarlessRestorePending = true });
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!;
        Assert.True(back.EarlessMiqoteHair);
        Assert.True(back.EarlessRestorePending);
    }

    [Fact]
    [Trait("Plan", "AC-refit-piercings-option-4")]
    [Trait("Plan", "AC-refit-piercings-option-5")]
    [Trait("Plan", "EDGE-refit-piercings-option-10")]
    public void Refit_piercings_default_off()
    {
        // [AC-refit-piercings-option-4] [AC-refit-piercings-option-5] [EDGE-refit-piercings-option-10] Off for a new
        // config and for one saved before the settings existed; a saved "on" is read back.
        var fresh = new Configuration();
        Assert.False(fresh.AutoRefitPiercings);
        Assert.False(fresh.SizeSetPiercings);

        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$"""{"Version":{{Configuration.CurrentVersion}},"SizeSetYas":true}""")!;
        old.Migrate();
        Assert.False(old.AutoRefitPiercings);
        Assert.False(old.SizeSetPiercings);

        var on = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$"""{"Version":{{Configuration.CurrentVersion}},"AutoRefitPiercings":true,"SizeSetPiercings":true}""")!;
        Assert.True(on.AutoRefitPiercings);
        Assert.True(on.SizeSetPiercings);

        Assert.False(new Proteus.Services.SizeSetRefit.Request("mod", [], true, false, []).Piercings);
    }

    [Fact]
    [Trait("Plan", "AC-size-set-keep-skin-1")]
    [Trait("Plan", "EDGE-size-set-keep-skin-1")]
    public void SizeSetKeepHiddenSkin_defaults_off_and_round_trips()
    {
        // [AC-size-set-keep-skin-1] [EDGE-size-set-keep-skin-1] Off for a new config and for one saved before the
        // setting existed, with no migration; a saved "on" is read back.
        Assert.False(new Configuration().SizeSetKeepHiddenSkin);

        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$"""{"Version":{{Configuration.CurrentVersion}},"SizeSetPiercings":true}""")!;
        old.Migrate();
        Assert.False(old.SizeSetKeepHiddenSkin);
        Assert.Equal(Configuration.CurrentVersion, old.Version);

        var on = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$"""{"Version":{{Configuration.CurrentVersion}},"SizeSetKeepHiddenSkin":true}""")!;
        on.Migrate();
        Assert.True(on.SizeSetKeepHiddenSkin);
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(on))!;
        Assert.True(back.SizeSetKeepHiddenSkin);
    }

    /// <summary>
    /// v7 -> v8 turns hat compatibility on for every existing config, once. A user who then switches it
    /// off keeps it off: the second migration sees a config already stamped current.
    /// </summary>
    [Fact]
    public void Hat_compat_is_turned_on_once_for_every_existing_config()
    {
        var config = new Configuration { Version = 7, AutoHatCompat = false };

        config.Migrate();
        Assert.True(config.AutoHatCompat);
        Assert.Equal(Configuration.CurrentVersion, config.Version);

        config.AutoHatCompat = false;   // as if the user then turned it off
        config.Migrate();
        Assert.False(config.AutoHatCompat);
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
    [Trait("Plan", "AC-gh33-36-37-body-size-8")]
    public void A_config_from_version_one_runs_every_step()   // [AC-gh33-36-37-body-size-8]
    {
        var config = new Configuration
        {
            Version = 1,
            EnableCompression = true,
            DisableAutoRedraw = true,
            AutoHatCompat = false,
            HideRedundantMeshes = false,
            RetargetBodyDir = "Neolithe",
        };
        config.RetargetTargets["_top"] = "chest/L.mdl";

        config.Migrate();

        Assert.Equal("chest/L.mdl", Proteus.Services.RememberedTargets.SizesOf(config, "Neolithe")!["_top"]);   // v9
        Assert.Equal("Neolithe", config.RetargetBodyByPart["_top"]);
        Assert.Empty(config.RetargetTargets);
        Assert.False(config.EnableCompression);        // v1 -> v2
        Assert.False(config.AutoRedraw);               // v3 -> v4, carried across from the opt-out
        Assert.True(config.HideRedundantMeshes);       // v6 -> v7
        Assert.True(config.AutoHatCompat);             // v8, after v6 forced it off
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

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    [Trait("Plan", "EDGE-womens-overlays-men-19")]
    public void Old_config_without_men_opt_out_overlays_men() // [AC-womens-overlays-men-5] [EDGE-womens-overlays-men-19]
    {
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            $$$"""{"Version":{{{Configuration.CurrentVersion}}},"SiblingSynthesis":{"Stockings":0}}""")!;
        Assert.Empty(old.MenOverlayOffMods);
        Assert.True(old.OverlaysMenFor("Stockings"));
        old.Migrate();
        Assert.Equal(Configuration.CurrentVersion, old.Version);
        Assert.True(new Configuration().OverlaysMenFor("Anything"));
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    [Trait("Plan", "EDGE-womens-overlays-men-19")]
    public void Men_opt_out_round_trips_case_insensitively() // [AC-womens-overlays-men-5] [EDGE-womens-overlays-men-19]
    {
        var c = new Configuration();
        c.MenOverlayOffMods.Add("Solona's Stockings - for Proteus");
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(Newtonsoft.Json.JsonConvert.SerializeObject(c))!;
        Assert.False(back.OverlaysMenFor("solona's stockings - FOR proteus"));
        Assert.True(back.OverlaysMenFor("Another Mod"));
        Assert.Single(back.MenOverlayOffMods);
    }
}
