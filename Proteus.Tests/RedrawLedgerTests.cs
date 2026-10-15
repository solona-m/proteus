using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The log of every redraw Proteus asks for and every one the character goes through, so an outfit change can be
/// counted from dalamud.log: which redraws were Proteus's, and what caused each.
/// </summary>
public class RedrawLedgerTests
{
    private long now = 10_000;
    private RedrawLedger Ledger() => new(() => now);

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-8")]
    [Trait("Plan", "EDGE-fewer-redraws-28")]
    public void Redraws_and_in_place_reloads_are_numbered_apart_with_their_cause()
    {
        var ledger = Ledger();

        Assert.Equal("[Proteus] redraw #1: composite (ModSettingChanged)", ledger.Redrew("composite (ModSettingChanged)", reached: true));
        Assert.Equal("[Proteus] reload #1 (in place): composite (glamourer-equip)", ledger.Reloaded("composite (glamourer-equip)"));
        Assert.Equal("[Proteus] redraw #2: auto refit", ledger.Redrew("auto refit", reached: true));
        Assert.Equal("[Proteus] redraw #3: hat compat (not drawn: Penumbra refused it)", ledger.Redrew("hat compat", reached: false));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-8")]
    public void A_redraw_the_character_goes_through_soon_after_ours_is_ours_once()
    {
        var ledger = Ledger();
        ledger.Redrew("auto refit", reached: true);

        now += 29;
        Assert.Equal("[Proteus] character redrawn (ours: redraw #1, 29 ms after it was asked for)", ledger.Observed());

        // A second redraw in the same window is someone else's: each of ours accounts for one.
        now += 500;
        Assert.Equal("[Proteus] character redrawn (not Proteus's — 529 ms since our last redraw)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-8")]
    public void A_redraw_long_after_ours_is_not_ours()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: true);

        now += 2_500;
        Assert.Equal("[Proteus] character redrawn (not Proteus's — 2500 ms since our last redraw)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-8")]
    public void A_redraw_before_any_of_ours_is_not_ours()
    {
        Assert.Equal("[Proteus] character redrawn (not Proteus's — none of ours yet)", Ledger().Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-8")]
    public void A_redraw_Penumbra_refused_claims_nothing()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: false);

        now += 30;
        Assert.Equal("[Proteus] character redrawn (not Proteus's — none of ours yet)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-8")]
    public void Only_the_latest_of_ours_is_matched()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: true);
        now += 100;
        ledger.Redrew("auto refit", reached: true);

        now += 40;
        Assert.Equal("[Proteus] character redrawn (ours: redraw #2, 40 ms after it was asked for)", ledger.Observed());
    }
}
