using System.Linq;
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
    [Trait("Plan", "AC-fewer-redraws-6")]
    [Trait("Plan", "EDGE-fewer-redraws-25")]
    public void Redraws_and_in_place_reloads_are_numbered_apart_with_their_cause()
    {
        var ledger = Ledger();

        Assert.Equal("[Proteus] redraw #1: composite (ModSettingChanged)", ledger.Redrew("composite (ModSettingChanged)", reached: true));
        Assert.Equal("[Proteus] reload #1 (in place): composite (glamourer-equip)", ledger.Reloaded("composite (glamourer-equip)"));
        Assert.Equal("[Proteus] redraw #2: auto refit", ledger.Redrew("auto refit", reached: true));
        Assert.Equal("[Proteus] redraw #3: hat compat (not drawn: Penumbra refused it)", ledger.Redrew("hat compat", reached: false));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
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
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_redraw_long_after_ours_is_not_ours()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: true);

        now += 2_500;
        Assert.Equal("[Proteus] character redrawn (not Proteus's — 2500 ms since our last redraw)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_redraw_before_any_of_ours_is_not_ours()
    {
        Assert.Equal("[Proteus] character redrawn (not Proteus's — none of ours yet)", Ledger().Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_redraw_Penumbra_refused_claims_nothing()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: false);

        now += 30;
        Assert.Equal("[Proteus] character redrawn (not Proteus's — none of ours yet)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void The_characters_redraw_arriving_before_our_call_returns_is_still_ours()
    {
        var ledger = Ledger();
        int n = ledger.Asking();

        now += 5;   // Penumbra raised the event while the worker was still waiting on the framework thread
        Assert.Equal("[Proteus] character redrawn (ours: redraw #1, 5 ms after it was asked for)", ledger.Observed());
        Assert.Equal("[Proteus] redraw #1: auto refit", ledger.Answered(n, "auto refit", reached: true));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_redraw_following_a_glamourer_change_we_asked_for_is_ours_under_its_cause()
    {
        var ledger = Ledger();
        Assert.Equal("[Proteus] asked: _rir carrier on (may redraw the character)", ledger.Expecting("_rir carrier on").Line);

        now += 120;
        Assert.Equal("[Proteus] character redrawn (ours: _rir carrier on, 120 ms after it was asked for)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_glamourer_change_that_failed_claims_nothing()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: true);
        now += 10;
        var (id, _) = ledger.Expecting("_rir carrier on");
        ledger.Withdraw(id);

        now += 20;
        Assert.Equal("[Proteus] character redrawn (ours: redraw #1, 30 ms after it was asked for)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_redraw_counted_against_a_claim_later_taken_back_is_not_counted_again()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: true);
        var (id, _) = ledger.Expecting("_rir carrier on");

        now += 30;
        Assert.StartsWith("[Proteus] character redrawn (ours", ledger.Observed());
        ledger.Withdraw(id);

        now += 100;
        Assert.Equal("[Proteus] character redrawn (not Proteus's — 130 ms since our last redraw)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void Two_overlapping_refused_redraws_bring_back_neither()
    {
        var ledger = Ledger();
        int first = ledger.Asking();
        int second = ledger.Asking();
        ledger.Answered(first, "composite", reached: false);
        ledger.Answered(second, "auto refit", reached: false);

        now += 30;
        Assert.Equal("[Proteus] character redrawn (not Proteus's — none of ours yet)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void The_window_ends_at_two_seconds()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: true);
        now += RedrawLedger.EchoWindowMs - 1;
        Assert.StartsWith("[Proteus] character redrawn (ours", ledger.Observed());

        ledger.Redrew("composite", reached: true);
        now += RedrawLedger.EchoWindowMs;
        Assert.StartsWith("[Proteus] character redrawn (not Proteus's", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_refused_redraw_leaves_the_claim_of_the_one_before_it()
    {
        var ledger = Ledger();
        ledger.Redrew("composite", reached: true);
        now += 10;
        ledger.Redrew("hat compat", reached: false);

        now += 20;
        Assert.Equal("[Proteus] character redrawn (ours: redraw #1, 30 ms after it was asked for)", ledger.Observed());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void Numbers_stay_unique_and_gapless_across_threads()
    {
        var ledger = Ledger();
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        System.Threading.Tasks.Parallel.For(0, 400, i =>
            lines.Add(i % 2 == 0 ? ledger.Redrew("r", reached: true) : ledger.Reloaded("l")));

        int Number(string line) => int.Parse(line[(line.IndexOf('#') + 1)..].Split(' ', ':')[0]);
        Assert.Equal(Enumerable.Range(1, 200), lines.Where(l => l.Contains("redraw #")).Select(Number).OrderBy(x => x));
        Assert.Equal(Enumerable.Range(1, 200), lines.Where(l => l.Contains("reload #")).Select(Number).OrderBy(x => x));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    [Trait("Plan", "EDGE-fewer-redraws-25")]
    public void A_batchs_hold_is_logged_from_start_to_summary()
    {
        var ledger = Ledger();
        ledger.Redrew("design restore", reached: true);
        Assert.Equal("[Proteus] redraws held: auto-refit#3 (an auto refit batch is running)", ledger.HoldStarted("auto-refit#3", out _));
        Assert.Equal("[Proteus] redraw held until the batch ends: auto refit: _top switched on",
                     ledger.Absorbed("auto refit: _top switched on"));

        var hold = new RedrawHold(() => now);
        hold.Hold("auto-refit#3", 30_000, 120_000);
        hold.TryAbsorbModelRedraw("auto refit: _top switched on");
        hold.TryDefer(new RedrawHold.Deferred("ModSettingChanged", true, false, false));
        now += 24_000;
        hold.Release("auto-refit#3", 0);
        Assert.Equal("[Proteus] redraws released after 24000 ms (auto-refit#3): 1 redraw(s), 1 composite trigger(s) "
                   + "waiting — ModSettingChanged", ledger.HoldReleased(hold.Poll()!));

        ledger.Redrew("auto refit batch", reached: true);
        ledger.Reloaded("composite (held)");
        ledger.Reloaded("composite (post-settle-correction)");
        Assert.Equal("[Proteus] outfit change (auto refit batch): 1 redraw(s), 2 in-place reload(s)", ledger.BatchSummary());
        Assert.Null(ledger.BatchSummary());   // once
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    [Trait("Plan", "EDGE-fewer-redraws-26")]
    public void A_batch_that_held_nothing_writes_no_summary()
    {
        var ledger = Ledger();
        ledger.HoldStarted("auto-refit#1", out _);
        ledger.Redrew("someone else's", reached: true);
        var hold = new RedrawHold(() => now);
        hold.Hold("auto-refit#1", 30_000, 120_000);
        hold.Release("auto-refit#1", 0);
        ledger.HoldReleased(hold.Poll()!);
        Assert.Null(ledger.BatchSummary());
        Assert.Null(Ledger().BatchSummary());   // no batch at all
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
    public void A_batch_starting_in_the_last_ones_tail_ends_that_summary_and_gets_its_own()
    {
        var ledger = Ledger();
        var hold = new RedrawHold(() => now);
        void Batch(string key, int redraws)
        {
            hold.Hold(key, 30_000, 120_000);
            hold.TryAbsorbModelRedraw("refit");
            hold.Release(key, 0);
            ledger.HoldReleased(hold.Poll()!);
            for (int i = 0; i < redraws; i++) ledger.Redrew("release", reached: true);
        }

        ledger.HoldStarted("auto-refit#1", out var none);
        Assert.Null(none);
        Batch("auto-refit#1", redraws: 2);

        ledger.HoldStarted("auto-refit#2", out var first);
        Assert.Equal("[Proteus] outfit change (auto refit batch): 2 redraw(s), 0 in-place reload(s)", first);
        Assert.Null(ledger.BatchSummary());   // batch 2 is still running
        Batch("auto-refit#2", redraws: 1);
        Assert.Equal("[Proteus] outfit change (auto refit batch): 1 redraw(s), 0 in-place reload(s)", ledger.BatchSummary());
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-7")]
    public void A_late_recheck_says_how_late()
    {
        Assert.Equal("[Proteus] ModSettingChanged:Setting:Chibi: a re-check landed 1800 ms after the batch let its "
                   + "redraws go — it runs on its own",
                     RedrawLedger.LateRecheck("ModSettingChanged:Setting:Chibi", 1800));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-6")]
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
