using Newtonsoft.Json;
using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Who gets shown the release notes, and once.
/// <para/>
/// The whole gate is <see cref="Configuration.WantsWhatsNew"/> against <see cref="Plugin.CurrentWhatsNew"/>, and it
/// has no migration step on purpose: a config written before the field existed simply has no such key, so it
/// deserializes to zero exactly as a brand-new one does, and both are shown the notes. That equivalence is
/// load-bearing and invisible in the source, so it is pinned here.
/// </summary>
public class WhatsNewTests
{
    /// <summary>A fresh install is shown the notes: the field's default is below the current generation.</summary>
    [Fact]
    public void A_new_config_is_shown_the_notes()
    {
        Assert.True(new Configuration().WantsWhatsNew(Plugin.CurrentWhatsNew));
    }

    /// <summary>
    /// The upgrade path, and the reason no migration step is needed. A config saved by a build that predates the
    /// field must land on the same zero a new config starts at.
    /// <para/>
    /// Deserialized with Newtonsoft, not System.Text.Json: this test is the only guard on the claim that carries the
    /// whole no-migration design, so it has to go through the same serializer <c>GetPluginConfig</c> uses. The two
    /// agree on a missing int today, which is exactly what makes testing the wrong one a silent trap.
    /// </summary>
    [Fact]
    public void A_config_saved_before_the_field_existed_is_shown_the_notes()
    {
        var older = JsonConvert.DeserializeObject<Configuration>("""{"Version":8,"PluginEnabled":true}""")!;

        Assert.Equal(0, older.WhatsNewShown);
        Assert.True(older.WantsWhatsNew(Plugin.CurrentWhatsNew));
    }

    /// <summary>Nobody is shown the same notes twice. The window writes this on close, not on open.</summary>
    [Fact]
    public void Reading_the_notes_closes_the_gate()
    {
        var config = new Configuration { WhatsNewShown = Plugin.CurrentWhatsNew };

        Assert.False(config.WantsWhatsNew(Plugin.CurrentWhatsNew));
    }

    /// <summary>
    /// The next release opens the window once more, without any new plumbing — which is why the field is an int and
    /// not a bool. The generation is passed in rather than compared against itself, so this fails if the gate ever
    /// stops depending on the argument.
    /// </summary>
    [Fact]
    public void A_later_generation_of_notes_is_shown_again()
    {
        var config = new Configuration { WhatsNewShown = Plugin.CurrentWhatsNew };

        Assert.False(config.WantsWhatsNew(Plugin.CurrentWhatsNew));
        Assert.True(config.WantsWhatsNew(Plugin.CurrentWhatsNew + 1));
    }

    /// <summary>
    /// An install that has read LATER notes than this build offers is left alone, so rolling a build back does not
    /// re-show notes the user has already seen.
    /// </summary>
    [Fact]
    public void An_install_ahead_of_this_build_is_not_shown_older_notes()
    {
        var config = new Configuration { WhatsNewShown = Plugin.CurrentWhatsNew + 1 };

        Assert.False(config.WantsWhatsNew(Plugin.CurrentWhatsNew));
    }

    /// <summary>
    /// Guards against someone later adding a <c>Migrate</c> step that stamps the field for existing configs: that
    /// would suppress the notes for precisely the people who have something new to read.
    /// </summary>
    [Fact]
    public void Migration_never_marks_the_notes_as_read()
    {
        var upgrading = new Configuration { Version = 7 };
        upgrading.Migrate();

        Assert.Equal(0, upgrading.WhatsNewShown);
        Assert.True(upgrading.WantsWhatsNew(Plugin.CurrentWhatsNew));
    }
}
