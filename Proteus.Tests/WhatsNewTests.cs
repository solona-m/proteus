using System;
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
    /// Each reader is shown every section newer than the notes they last closed, and only those: someone who missed
    /// the body refit notes gets them with the hair notes, someone who read them gets the hair notes alone. A fresh
    /// install is shown everything.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-whats-new-5-1")]
    [Trait("Plan", "AC-whats-new-5-2")]
    [Trait("Plan", "AC-whats-new-5-3")]
    [Trait("Plan", "EDGE-whats-new-5-1")]
    [Trait("Plan", "EDGE-whats-new-5-2")]
    [Trait("Plan", "EDGE-whats-new-5-3")]
    public void Each_reader_is_shown_only_what_is_new_to_them()
    {
        const int first = Gui.WhatsNewWindow.FirstNotes, refit = Gui.WhatsNewWindow.AutoRefitNotes,
                  hair = Gui.WhatsNewWindow.HairPortNotes;

        // Never read anything: every section.
        Assert.True(Gui.WhatsNewWindow.IsNew(first, 0));
        Assert.True(Gui.WhatsNewWindow.IsNew(refit, 0));
        Assert.True(Gui.WhatsNewWindow.IsNew(hair, 0));

        // Read the first notes only: the refit and the hair, not the first three again.
        var readFirst = new Configuration { WhatsNewShown = first };
        Assert.True(readFirst.WantsWhatsNew(Plugin.CurrentWhatsNew));
        Assert.False(Gui.WhatsNewWindow.IsNew(first, readFirst.WhatsNewShown));
        Assert.True(Gui.WhatsNewWindow.IsNew(refit, readFirst.WhatsNewShown));
        Assert.True(Gui.WhatsNewWindow.IsNew(hair, readFirst.WhatsNewShown));

        // Read the refit notes: the window opens again, with the hair alone.
        var readRefit = new Configuration { WhatsNewShown = refit };
        Assert.True(readRefit.WantsWhatsNew(Plugin.CurrentWhatsNew));
        Assert.False(Gui.WhatsNewWindow.IsNew(refit, readRefit.WhatsNewShown));
        Assert.True(Gui.WhatsNewWindow.IsNew(hair, readRefit.WhatsNewShown));

        // Read the hair notes: the window opens again, with the colour rows, hair across the sexes, gear variants and
        // the shader upgrade alone.
        const int rows = Gui.WhatsNewWindow.ColorRowsNotes;
        var readHair = new Configuration { WhatsNewShown = hair };
        Assert.True(readHair.WantsWhatsNew(Plugin.CurrentWhatsNew));
        Assert.False(Gui.WhatsNewWindow.IsNew(hair, readHair.WhatsNewShown));
        Assert.True(Gui.WhatsNewWindow.IsNew(rows, readHair.WhatsNewShown));
        Assert.True(Gui.WhatsNewWindow.IsNew(rows, 0));

        // Read the colour-row notes: the window opens again, with matcaps and the "All" mode alone.
        const int matcaps = Gui.WhatsNewWindow.MatcapsNotes;
        var readRows = new Configuration { WhatsNewShown = rows };
        Assert.True(readRows.WantsWhatsNew(Plugin.CurrentWhatsNew));
        Assert.False(Gui.WhatsNewWindow.IsNew(rows, readRows.WhatsNewShown));
        Assert.True(Gui.WhatsNewWindow.IsNew(matcaps, readRows.WhatsNewShown));
        Assert.True(Gui.WhatsNewWindow.IsNew(matcaps, 0));
        Assert.True(Gui.WhatsNewWindow.IsNew(matcaps, first));
        Assert.True(Gui.WhatsNewWindow.IsNew(rows, first));

        // The matcap sections belong to the current notes, so reading them retires them; an install ahead of this
        // build is shown nothing.
        Assert.Equal(Plugin.CurrentWhatsNew, matcaps);
        Assert.False(Gui.WhatsNewWindow.IsNew(matcaps, Plugin.CurrentWhatsNew));
        Assert.False(new Configuration { WhatsNewShown = Plugin.CurrentWhatsNew + 1 }.WantsWhatsNew(Plugin.CurrentWhatsNew));
    }

    /// <summary>
    /// The newest notes are drawn at the top: matcaps, then the "All" mode, above the colour-row generation. Hair
    /// across the sexes stays a fourth-generation section and is not announced a second time.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-whats-new-5-2")]
    [Trait("Plan", "AC-whats-new-5-7")]
    [Trait("Plan", "EDGE-whats-new-5-9")]
    public void Newest_notes_are_drawn_first_and_hair_across_the_sexes_once()
    {
        string src = System.IO.File.ReadAllText(ShaderUpgradePlanTests.SourceFile("Proteus/Gui/WhatsNewWindow.cs"));
        int gen5 = src.IndexOf("IsNew(MatcapsNotes, read)", StringComparison.Ordinal);
        int gen4 = src.IndexOf("IsNew(ColorRowsNotes, read)", StringComparison.Ordinal);
        int matcaps = src.IndexOf("Section(s.MatcapsHead, s.MatcapsBody)", StringComparison.Ordinal);
        int all = src.IndexOf("Section(s.MoveAllHead, s.MoveAllBody)", StringComparison.Ordinal);

        Assert.True(gen5 >= 0 && gen4 >= 0 && matcaps >= 0 && all >= 0, "a generation or section is missing");
        Assert.True(gen5 < matcaps && matcaps < all && all < gen4,
            "the fifth notes must be drawn first, matcaps before the All mode");

        int hair = src.IndexOf("s.HairOtherSexHead", StringComparison.Ordinal);
        Assert.True(hair > gen4, "hair across the sexes belongs to the fourth notes");
        Assert.Equal(-1, src.IndexOf("s.HairOtherSexHead", hair + 1, StringComparison.Ordinal));
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
