using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// How a size set breaks a tie between mods a garment matches equally: it passes "a body mod the size table knows"
/// (<see cref="SizeSetRefit.Request.KnownBody"/>) where the automatic refit passes "switched on", so the body mod wins
/// over an outfit or underwear mod shipping the same skin. Equinox's Rue Large shared 55.2% with Rue+ and with "Elegy -
/// Sylvie Bra &amp; Panties", which ships three bodies' skin and so has more sizes: read on Elegy, its Rosaline Coconut size
/// was cut from the wrong body.
/// </summary>
public class SizeSetKnownBodyTests
{
    private static AutoRefitDecisions.ModShare Share(string dir, float share, bool known, int sizes)
        => new(dir, share, Preferred: false, Enabled: known, Sizes: sizes);

    [Fact]
    [Trait("Plan", "AC-equinox-legs-slits-8")]
    public void At_a_tie_the_known_body_beats_a_bigger_pack_that_ships_its_skin()
    {
        Assert.Equal("hs-Rue+-2.2.7-y0f", AutoRefitDecisions.Choose([
            Share("Elegy - Sylvie Bra & Panties (RueYABUranusRe)", 0.552f, known: false, sizes: 60),
            Share("hs-Rue+-2.2.7-y0f", 0.552f, known: true, sizes: 24),
        ]));
    }

    [Fact]
    [Trait("Plan", "AC-equinox-legs-slits-8")]
    [Trait("Plan", "EDGE-equinox-legs-slits-29")]
    [Trait("Plan", "EDGE-equinox-legs-slits-30")]
    public void Without_a_tie_the_share_still_decides()
    {
        // A garment made on the underwear mod itself shares clearly more with it: that is still its body.
        Assert.Equal("Elegy - Sylvie Bra & Panties (RueYABUranusRe)", AutoRefitDecisions.Choose([
            Share("Elegy - Sylvie Bra & Panties (RueYABUranusRe)", 0.70f, known: false, sizes: 60),
            Share("hs-Rue+-2.2.7-y0f", 0.552f, known: true, sizes: 24),
        ]));
        // Two known bodies at a tie: the bigger pack, as before.
        Assert.Equal("hs-Yet Another Body+-4.2.0-tmi", AutoRefitDecisions.Choose([
            Share("hs-Rue+-2.2.7-y0f", 0.552f, known: true, sizes: 24),
            Share("hs-Yet Another Body+-4.2.0-tmi", 0.552f, known: true, sizes: 40),
        ]));
    }
}
