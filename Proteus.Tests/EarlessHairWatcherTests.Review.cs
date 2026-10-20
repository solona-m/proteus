using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>The automatic path's failures and the panel's view of them, after the code review.</summary>
public partial class EarlessHairWatcherTests
{
    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-33")]
    public void Unreadable_record_at_an_equip_restore_asks_again_later()
    {
        using var s = new Setup();
        s.Run();
        s.Reloaded();
        File.WriteAllText(Path.Combine(s.ModRoot, "Proteus", EarlessHairRecord.RecordFile), "{ not json");
        s.Host.Calls.Clear();
        s.Host.Earless = false;
        string? key = null;
        var view = HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.True(view.Failed);
        Assert.Null(key);
        Assert.Equal(["look-again"], s.Host.Calls);
        Assert.Equal("Proteus Earless/long.mdl", s.Serves());
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-9")]
    [Trait("Plan", "EDGE-earless-miqote-hair-13")]
    public void A_save_that_fails_reaches_the_panel_and_says_nothing_in_chat()
    {
        using var s = new Setup();
        EarlessHairWriter.BeforeSwap = _ => throw new IOException("disk full");
        HairPortWatcher.View view;
        try { view = s.Run(); }
        finally { EarlessHairWriter.BeforeSwap = null; }
        Assert.True(view.Failed);
        Assert.Contains("disk full", view.Message);
        Assert.False(view.Earless);
        Assert.Empty(s.Host.Calls);
        Assert.Equal("long.mdl", s.Serves());
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-31")]
    public void A_mod_that_fails_after_its_swaps_is_still_reloaded_and_the_pass_goes_on()
    {
        using var s = new Setup();
        var other = s.AddMod("Other Hair", Worn, EaredHairModel(801, radius: 0.104f));
        s.Run();
        Assert.True(EarlessHairWriter.Save(other, EarlessHairBuilder.Build(other, Worn).Writes).Ok);
        s.Host.Calls.Clear();
        EarlessHairWriter.AfterSwaps = () => throw new IOException("locked");
        HairPortWatcher.PassResult pass;
        try { pass = HairPortWatcher.RestorePass(s.Host, s.ModsRoot, Worn, () => false); }
        finally { EarlessHairWriter.AfterSwaps = null; }
        Assert.True(pass.Completed);
        Assert.Equal(2, pass.Restored);
        Assert.Equal(1, s.Host.Calls.Count(c => c == "reload Miqo Hair"));
        Assert.Equal(1, s.Host.Calls.Count(c => c == "reload Other Hair"));
        // The ears are back: nothing to tell the player, only the log.
        Assert.Empty(pass.Blocked);
        Assert.DoesNotContain("announce-earless", s.Host.Calls);
        Assert.Equal(2, s.Host.Lines.Count(l => l.Contains("record was not updated")));
        Assert.Equal("long.mdl", s.Serves());
        Assert.Equal("long.mdl", s.Serves(other));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-31")]
    public void Blocked_mods_reach_the_panel()
    {
        using var s = new Setup();
        s.Run();
        File.Delete(Path.Combine(s.ModRoot, "long.mdl"));
        var pass = HairPortWatcher.RestorePass(s.Host, s.ModsRoot, Worn, () => false);
        var view = HairPortWatcher.PassView(pass, Worn);
        Assert.True(view.Failed);
        Assert.Contains("Miqo Hair", view.Message);
        Assert.Contains("long.mdl", view.Message);

        var clean = HairPortWatcher.PassView(new HairPortWatcher.PassResult(3, true, [], [], true, []), Worn);
        Assert.False(clean.Failed);
        Assert.Equal(string.Format(HairPortWatcher.RestoredFmt, 3), clean.Message);
    }

    [Theory]
    [Trait("Plan", "AC-earless-miqote-hair-9")]
    [Trait("Plan", "EDGE-earless-miqote-hair-25")]
    [InlineData(true)]
    [InlineData(false)]
    public void The_view_names_the_mods_own_earless_option_with_the_setting_on_or_off(bool on)
    {
        using var s = new Setup();
        s.AddMod("Miqo Hair", Worn, EaredHairModel(801), Group("Races", "Single",
            ("Long", new() { [Worn] = "long.mdl" }), ("Miqote Earless", new() { [Worn] = "earless.mdl" })));
        File.WriteAllBytes(Path.Combine(s.ModRoot, "earless.mdl"), EarlessHairModel(801));
        s.Host.Earless = on;
        var view = s.Run();
        Assert.Equal(["Races / Miqote Earless"], view.EarlessOptions);
        Assert.Equal(on, view.Earless);
    }
}
