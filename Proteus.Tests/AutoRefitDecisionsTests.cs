using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Proteus.Services;
using Xunit;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Worn = Proteus.Services.AutoRefitDecisions.Worn;

namespace Proteus.Tests;

/// <summary>
/// The choices the automatic body refit makes without asking: which size goes with which, which body a garment was made
/// on, and which newly worn pieces it may touch at all.
/// </summary>
public class AutoRefitDecisionsTests
{
    private static BodyOption Opt(string name, string rel, string section = "", string group = "Chest",
                                  string slot = "_top", string race = "0201")
        => new(group, section, name, rel, $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl", slot);

    private static BodySizeMatch.Ranking Ranked(Confidence confidence, params (BodyOption Option, float Rms)[] scores)
        => new(confidence, scores.Select(s => new BodySizeMatch.Score(s.Option, 0f, s.Rms)).ToList());

    // ── sizes nobody chose ───────────────────────────────────────────────────

    [Fact]
    public void Hands_follow_the_chest_by_heading_and_name()
    {
        // Neolithe reuses one name under every heading: "SFW M" under DEFAULT is not "SFW M" under NEOBELLY.
        var chest = Opt("SFW M", "chest/neo/m.mdl", "--- NEOBELLY ---");
        var hands = new[]
        {
            Opt("SFW M", "hands/default/m.mdl", "--- DEFAULT ---", "Hands", "_glv"),
            Opt("SFW M", "hands/neo/m.mdl", "--- NEOBELLY ---", "Hands", "_glv"),
        };

        Assert.Equal("hands/neo/m.mdl", AutoRefitDecisions.Companion(hands, chest, worn: null)!.Rel);
    }

    [Fact]
    public void Hands_fall_back_by_name_then_heading_then_what_is_worn_then_the_first()
    {
        var chest = Opt("Large", "chest/l.mdl", "Curvy");
        var byName = new[] { Opt("Small", "h/s.mdl", group: "Hands", slot: "_glv"), Opt("Large", "h/l.mdl", group: "Hands", slot: "_glv") };
        Assert.Equal("h/l.mdl", AutoRefitDecisions.Companion(byName, chest, null)!.Rel);

        var byHeading = new[] { Opt("Nails", "h/plain.mdl", "Plain", "Hands", "_glv"), Opt("Nails", "h/curvy.mdl", "Curvy", "Hands", "_glv") };
        Assert.Equal("h/curvy.mdl", AutoRefitDecisions.Companion(byHeading, chest, null)!.Rel);

        var unrelated = new[] { Opt("Short", "h/short.mdl", group: "Hands", slot: "_glv"), Opt("Long", "h/long.mdl", group: "Hands", slot: "_glv") };
        Assert.Equal("h/long.mdl", AutoRefitDecisions.Companion(unrelated, chest, worn: unrelated[1])!.Rel);
        Assert.Equal("h/short.mdl", AutoRefitDecisions.Companion(unrelated, chest, worn: null)!.Rel);
        Assert.Null(AutoRefitDecisions.Companion([], chest, null));
    }

    [Fact]
    public void A_remembered_size_is_found_by_file_and_at_another_race_by_name()
    {
        var m = Opt("SFW M", "chest/c0201/m.mdl", "Default");
        var size = AutoRefitDecisions.RefOf(m);

        Assert.Same(m, AutoRefitDecisions.Resolve([Opt("SFW S", "chest/c0201/s.mdl", "Default"), m], size));

        // The same option at a baked race points at another file.
        var other = Opt("SFW M", "chest/c1801/m.mdl", "Default", race: "1801");
        Assert.Same(other, AutoRefitDecisions.Resolve([Opt("SFW S", "chest/c1801/s.mdl", "Default", race: "1801"), other], size));

        Assert.Null(AutoRefitDecisions.Resolve([Opt("Other", "x.mdl")], size));
        Assert.Null(AutoRefitDecisions.Resolve([m], null));
    }

    [Fact]
    public void A_remembered_file_matches_whatever_slashes_and_case_the_manifest_uses()
    {
        var m = Opt("M", @"Chest\Sizes\M.mdl");
        Assert.Same(m, AutoRefitDecisions.Resolve([m], new BodySizeRef { Rel = "chest/sizes/m.mdl" }));
    }

    // ── which body it was made on ────────────────────────────────────────────

    private static AutoRefitDecisions.ModShare Share(string dir, float share, bool preferred = false,
                                                     bool enabled = false, int sizes = 10)
        => new(dir, share, preferred, enabled, sizes);

    [Fact]
    public void A_mod_is_sampled_at_its_first_middle_and_last_size()
    {
        var o = Enumerable.Range(0, 7).Select(i => Opt($"S{i}", $"s{i}.mdl")).ToArray();
        Assert.Equal([o[0], o[3], o[6]], AutoRefitDecisions.SamplesOf(o));
        Assert.Equal([o[0], o[1]], AutoRefitDecisions.SamplesOf(o.Take(2).ToList()));
    }

    [Fact]
    public void The_family_is_every_mod_near_the_best_sampled_share()
    {
        // Shares measured on "This Old Thing"'s LaRue chest: the Rue-based bodies share its mesh, Neolithe does not.
        var family = AutoRefitDecisions.Family([
            Share("Rue", 0.41f), Share("Neolithe", 0.00f), Share("LavaBod", 0.45f), Share("Uranus", 0.32f),
            Share("Bibo", 0.01f),
        ]);
        Assert.Equal(["Rue", "LavaBod"], family);
    }

    [Fact]
    public void A_garment_matching_no_installed_body_has_no_family()
    {
        Assert.Empty(AutoRefitDecisions.Family([Share("Rue", 0.01f), Share("Neolithe", 0.02f)]));
        Assert.Empty(AutoRefitDecisions.Family([]));
        Assert.Null(AutoRefitDecisions.Choose([Share("Rue", 0.01f)]));
    }

    [Fact]
    public void The_mod_sharing_the_most_over_every_size_is_chosen()
    {
        // LavaBod+ ships LaRue's own sizes, so a LaRue garment shares more with it than with Rue itself.
        Assert.Equal("LavaBod", AutoRefitDecisions.Choose([Share("Rue", 0.406f, preferred: true), Share("LavaBod", 0.558f)]));
    }

    [Fact]
    public void Among_equal_shares_the_preferred_body_then_one_switched_on_then_the_bigger_pack_wins()
    {
        // A body and its fork share exactly as much; so does a one-body mod copied from it.
        Assert.Equal("Neolithe YAS", AutoRefitDecisions.Choose([
            Share("Neolithe", 0.423f, enabled: true, sizes: 114), Share("Neolithe YAS", 0.423f, preferred: true, sizes: 114),
        ]));
        Assert.Equal("Neolithe", AutoRefitDecisions.Choose([
            Share("Neolithe YAS", 0.423f, sizes: 114), Share("Neolithe", 0.423f, enabled: true, sizes: 114),
        ]));
        Assert.Equal("Neolithe", AutoRefitDecisions.Choose([
            Share("ProteusDress", 0.430f, sizes: 1), Share("Neolithe", 0.423f, sizes: 114),
        ]));
        // Beyond the tie margin the share decides, whatever else is true.
        Assert.Equal("LavaBod", AutoRefitDecisions.Choose([
            Share("Rue", 0.41f, preferred: true, enabled: true), Share("LavaBod", 0.45f),
        ]));
    }

    [Fact]
    public void Only_a_reading_with_something_behind_it_proceeds()
    {
        Assert.True(AutoRefitDecisions.Proceed(Confidence.Exact));
        Assert.True(AutoRefitDecisions.Proceed(Confidence.Likely));
        Assert.True(AutoRefitDecisions.Proceed(Confidence.Guess));
        Assert.False(AutoRefitDecisions.Proceed(Confidence.Ambiguous));
        Assert.False(AutoRefitDecisions.Proceed(Confidence.TooLittle));
        Assert.False(AutoRefitDecisions.Proceed(Confidence.NoBodyMesh));
    }

    [Fact]
    public void The_lower_of_two_readings_is_the_less_sure()
    {
        Assert.Equal(Confidence.Guess, AutoRefitDecisions.Lower(Confidence.Exact, Confidence.Guess));
        Assert.Equal(Confidence.Ambiguous, AutoRefitDecisions.Lower(Confidence.Ambiguous, Confidence.Likely));
        Assert.Equal(Confidence.TooLittle, AutoRefitDecisions.Lower(Confidence.TooLittle, Confidence.Ambiguous));
    }

    // ── the other parts ──────────────────────────────────────────────────────

    [Fact]
    public void Another_part_joins_on_a_clear_reading_or_a_guess_that_agrees_with_the_garments_own()
    {
        var chest = Opt("SFW M", "c/m.mdl", "Default");
        var legsM = Opt("SFW M", "l/m.mdl", "Default", "Legs", "_dwn");
        var legsL = Opt("SFW L", "l/l.mdl", "Default", "Legs", "_dwn");

        Assert.True(AutoRefitDecisions.AcceptOther(Ranked(Confidence.Likely, (legsL, 1f)), chest));
        Assert.True(AutoRefitDecisions.AcceptOther(Ranked(Confidence.Guess, (legsM, 1f)), chest));
        Assert.False(AutoRefitDecisions.AcceptOther(Ranked(Confidence.Guess, (legsL, 1f)), chest));
        Assert.False(AutoRefitDecisions.AcceptOther(Ranked(Confidence.Ambiguous, (legsM, 1f)), chest));
        Assert.False(AutoRefitDecisions.AcceptOther(Ranked(Confidence.TooLittle), chest));
    }

    [Fact]
    public void A_part_already_on_the_target_size_has_nothing_to_refit()
    {
        var m = Opt("M", @"Chest\M.mdl");
        Assert.True(AutoRefitDecisions.SameFile("Rue", m, "rue", Opt("M", "chest/m.mdl")));
        Assert.False(AutoRefitDecisions.SameFile("Rue", m, "Neolithe", Opt("M", "chest/m.mdl")));
        Assert.False(AutoRefitDecisions.SameFile("Rue", m, "Rue", Opt("L", "chest/l.mdl")));
    }

    // ── which pieces it may touch ────────────────────────────────────────────

    private const string Coat = "chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string Trousers = "chara/equipment/e6255/model/c0201e6255_dwn.mdl";

    [Fact]
    public void The_first_look_takes_note_and_refits_nothing()
    {
        var now = new Dictionary<string, Worn> { ["_top"] = new(Coat, "Old Thing", "top/m.mdl") };
        Assert.Empty(AutoRefitDecisions.Changed(null, now));
    }

    [Fact]
    public void A_new_piece_or_a_new_mod_drawing_it_counts_and_another_option_of_the_same_mod_does_not()
    {
        var before = new Dictionary<string, Worn>
        {
            ["_top"] = new(Coat, "Old Thing", "top/m.mdl"),
            ["_dwn"] = new(Trousers, "Old Thing", "dwn/m.mdl"),
        };

        // The player picking the author's L in Penumbra: their choice, never overridden.
        var otherSize = new Dictionary<string, Worn>(before) { ["_top"] = new(Coat, "Old Thing", "top/l.mdl") };
        Assert.Empty(AutoRefitDecisions.Changed(before, otherSize));

        var otherMod = new Dictionary<string, Worn>(before) { ["_top"] = new(Coat, "Another Coat", "top.mdl") };
        Assert.Equal(["_top"], AutoRefitDecisions.Changed(before, otherMod));

        var otherItem = new Dictionary<string, Worn>(before)
        {
            ["_dwn"] = new("chara/equipment/e0100/model/c0201e0100_dwn.mdl", null, null),
        };
        Assert.Equal(["_dwn"], AutoRefitDecisions.Changed(before, otherItem));

        // Put back on after going bare.
        var bare = new Dictionary<string, Worn> { ["_dwn"] = before["_dwn"] };
        Assert.Equal(["_top"], AutoRefitDecisions.Changed(bare, before));
    }

    [Fact]
    public void Proteus_own_mod_and_its_own_refits_are_never_refitted()
    {
        Assert.Equal(AutoRefitDecisions.Skip.Managed, AutoRefitDecisions.ShouldRefit(
            new Worn(Coat, SidecarDiscoveryService.ManagedModDir, "x.mdl"), true, false));
        Assert.Equal(AutoRefitDecisions.Skip.OwnRefit, AutoRefitDecisions.ShouldRefit(
            new Worn(Coat, "Old Thing", @"Body Retarget\Rue — M\c0201e6255_top.mdl"), true, false));
        Assert.Equal(AutoRefitDecisions.Skip.None, AutoRefitDecisions.ShouldRefit(
            new Worn(Coat, "Old Thing", "Body Retargets/top.mdl"), true, false));
    }

    [Fact]
    public void The_games_own_gear_waits_for_its_own_switch()
    {
        var vanilla = new Worn(Coat, null, null);
        Assert.Equal(AutoRefitDecisions.Skip.VanillaOff, AutoRefitDecisions.ShouldRefit(vanilla, false, false));
        Assert.Equal(AutoRefitDecisions.Skip.None, AutoRefitDecisions.ShouldRefit(vanilla, true, false));
        // Redirected somewhere that is neither a mod nor the game: not ours to judge.
        Assert.Equal(AutoRefitDecisions.Skip.Unknown, AutoRefitDecisions.ShouldRefit(vanilla, true, true));
    }

    [Fact]
    public void Picking_original_or_the_author_s_size_after_a_refit_is_the_player_s_choice()
    {
        string[] refits = ["Rue+ — Large", "Rue+ — Medium"];

        // Switched on here before, now "Original" (the game's model) or the author's own size: leave it.
        Assert.True(AutoRefitDecisions.ChoseOtherwise(true, ["Original"], refits));
        Assert.True(AutoRefitDecisions.ChoseOtherwise(true, ["Author M"], refits));
        Assert.True(AutoRefitDecisions.ChoseOtherwise(true, null, refits));
        // Still on one of the refits (any size, any case): not a choice against it.
        Assert.False(AutoRefitDecisions.ChoseOtherwise(true, ["rue+ — medium"], refits));
        Assert.False(AutoRefitDecisions.ChoseOtherwise(true, ["Author M", "Rue+ — Large"], refits));
        // Never switched on in this collection — made in another one: it goes on.
        Assert.False(AutoRefitDecisions.ChoseOtherwise(false, ["Author M"], refits));
    }

    // ── settings ─────────────────────────────────────────────────────────────

    [Fact]
    public void Preferences_survive_a_save_and_stay_case_insensitive()
    {
        var id = Guid.NewGuid();
        var config = new Configuration { AutoRefitEnabled = true };
        config.AutoRefitByCollection[id.ToString("D")] = new AutoRefitPreference
        {
            BodyDir = "Rue",
            Chest = new BodySizeRef { Rel = "chest/m.mdl", Name = "M" },
        };
        config.AutoRefitByCollection[id.ToString("D")].SwitchedOn.Add(AutoRefitDecisions.GroupKey("Old Thing", "Top"));
        config.AutoRefitByCollection[id.ToString("D")].Before[AutoRefitDecisions.GroupKey("Old Thing", "Top")] = ["Medium"];
        config.AutoRefitByCollection[id.ToString("D")].HeldBefore[AutoRefitDecisions.GroupKey("Old Thing", "Top")] = ["Small"];
        config.AutoRefitByCollection[id.ToString("D")].InheritedBefore.Add("Old Thing");
        config.AutoRefitByCollection[id.ToString("D")].Declined.Add(
            new AutoRefitDecisions.Worn("chara/equipment/e6255/model/c0201e6255_top.mdl", "Old Thing", "top.mdl").Key);

        var back = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(config))!;

        Assert.True(back.AutoRefitEnabled);
        Assert.False(back.AutoRefitVanilla);
        Assert.True(back.AutoRefitByCollection.TryGetValue(id.ToString("D").ToUpperInvariant(), out var pref));
        Assert.Equal("Rue", pref!.BodyDir);
        Assert.Equal("chest/m.mdl", pref.Chest!.Rel);
        Assert.Null(pref.Legs);
        Assert.Contains(AutoRefitDecisions.GroupKey("old thing", "TOP"), pref.SwitchedOn);
        // What was ticked before the refit, for undo — found whatever the case.
        Assert.Equal(["Medium"], pref.Before[AutoRefitDecisions.GroupKey("old thing", "TOP")]);
        // And the design hold's, apart from the collection's own.
        Assert.Equal(["Small"], pref.HeldBefore[AutoRefitDecisions.GroupKey("old thing", "TOP")]);
        // And that the collection had no setting of its own there, so undo hands the mod back to inheritance.
        Assert.Contains("old thing", pref.InheritedBefore);
        // A piece whose refit was undone stays declined, by what the slot draws.
        Assert.Contains(new AutoRefitDecisions.Worn("chara/equipment/e6255/model/c0201e6255_top.mdl", "old thing", null).Key,
                        pref.Declined);
    }

    [Fact]
    public void A_config_from_before_the_feature_has_it_off()
    {
        var older = JsonConvert.DeserializeObject<Configuration>("""{"Version":8,"PluginEnabled":true}""")!;
        Assert.False(older.AutoRefitEnabled);
        Assert.False(older.AutoRefitVanilla);
        Assert.Empty(older.AutoRefitByCollection);
    }
}
