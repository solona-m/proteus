using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.Interop;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The temporary mod-settings hold selecting a mod's options (<c>"options"</c> on <c>POST /actions/mod-settings</c>): so
/// in-game QA can switch a mod's size option without writing the user's settings. Never saved; put-back removes it.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessOptionHoldTests : IDisposable
{
    private const string Size = "Body — Skirt / Model(s)";
    private readonly HarnessFixture fx = new();
    private FakeGame Game => fx.Game;
    private Guid Coll => Game.Collection!.Value;

    public HarnessOptionHoldTests()
    {
        string root = fx.AddMod("Skirt");
        File.WriteAllText(Path.Combine(root, "meta.json"), """
            {"FileVersion":3,"Groups":[
              {"Name":"Body — Skirt / Model(s)","Type":"Single","Options":[{"Name":"Bibo S"},{"Name":"Rue WC"},{"Name":"Tre Medium"}]},
              {"Name":"Extras","Type":"Multi","Options":[{"Name":"Belt"},{"Name":"Bow"}]},
              {"Name":"Lace","Type":"Imc","Options":[{"Name":"A"}]}]}
            """);
        Game.Settings["Skirt"] = new(PenumbraBridge.SettingsReadKind.Ok,
            new(true, 530, new Dictionary<string, List<string>> { [Size] = ["Bibo S"], ["Extras"] = ["Belt"] }, false, false));
    }

    public void Dispose() => fx.Dispose();

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;
    private static string Message(JsonElement body) => body.GetProperty("error").GetProperty("message").GetString()!;

    private static Dictionary<string, string[]> Pairs(JsonElement list)
        => list.EnumerateArray().ToDictionary(p => p.GetProperty("group").GetString()!,
                                              p => p.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToArray());

    [Theory]
    [Trait("Plan", "AC-harness-option-hold-1")]
    [Trait("Plan", "AC-harness-option-hold-5")]
    [Trait("Plan", "EDGE-harness-option-hold-1")]
    [InlineData("""{"mod":"Skirt","options":{"Body \u2014 Skirt / Model(s)":["Rue WC"]}}""")]
    [InlineData("{\"mod\":\"Skirt\",\"options\":{\"Body — Skirt / Model(s)\":[\"Rue WC\"]}}")]
    public async Task Hold_options_selects_the_group_keeps_the_rest_and_lists_it(string json)
    {
        var (status, body) = await fx.Post("/actions/mod-settings", json);
        Assert.Equal(200, status);
        var echo = Pairs(body.GetProperty("options"));
        Assert.Equal(["Rue WC"], echo[Size]);
        Assert.Equal(["Belt"], echo["Extras"]);
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(530, body.GetProperty("priority").GetInt32());
        var sent = Game.HoldSets.Single();
        Assert.Equal(["Rue WC"], sent.Options[Size]);
        Assert.Equal(["Belt"], sent.Options["Extras"]);
        Assert.Equal(HarnessChanges.HoldSource, sent.Source);
        Assert.Equal(HarnessChanges.HoldKey, sent.Key);

        var (_, state) = await fx.Get("/state");
        var hold = state.GetProperty("harnessChanges").GetProperty("holds")[0];
        var listed = Pairs(hold.GetProperty("options"));
        Assert.Equal(["Rue WC"], listed[Size]);
        Assert.False(listed.ContainsKey("Extras"));   // only what the harness selected
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-25")]
    public async Task Hold_options_leaves_the_read_snapshot_unchanged()
    {
        var stored = Game.Settings["Skirt"].Settings!.Value.Options;
        await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Tre Medium"]}}""");
        Assert.Equal(["Bibo S"], stored[Size]);
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-2")]
    [Trait("Plan", "EDGE-harness-option-hold-21")]
    public async Task Put_back_clears_an_options_hold_with_temporary_calls_only()
    {
        Assert.Equal(200, (await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Bibo S"]}}""")).Status);
        Assert.True(Game.Temporary.ContainsKey((Coll, "Skirt")));
        var (status, report) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, status);
        Assert.Equal("cleared", report.GetProperty("holds")[0].GetProperty("result").GetString());
        Assert.False(Game.Temporary.ContainsKey((Coll, "Skirt")));
        Assert.DoesNotContain(Game.Calls, c => c.Member is nameof(IHarnessGame.AfterModChange));
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-5")]
    [Trait("Plan", "EDGE-harness-option-hold-14")]
    public async Task A_second_hold_keeps_the_earlier_selection()
    {
        await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Rue WC"]}}""");
        // Penumbra now reports the harness's own temporary setting.
        Game.Settings["Skirt"] = new(PenumbraBridge.SettingsReadKind.Ok,
            new(true, 530, new Dictionary<string, List<string>> { [Size] = ["Rue WC"], ["Extras"] = ["Belt"] }, false, true));
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal(200, status);
        var sent = Game.HoldSets.Last();
        Assert.Equal(["Rue WC"], sent.Options[Size]);
        Assert.Equal(["Bow"], sent.Options["Extras"]);
        Assert.True(body.GetProperty("before").GetProperty("configured").GetBoolean());
        Assert.False(body.GetProperty("before").GetProperty("temporary").GetBoolean());   // the first read's, not rebuilt
        var (_, state) = await fx.Get("/state");
        var listed = Pairs(state.GetProperty("harnessChanges").GetProperty("holds")[0].GetProperty("options"));
        Assert.Equal(["Rue WC"], listed[Size]);
        Assert.Equal(["Bow"], listed["Extras"]);
    }

    [Theory]
    [Trait("Plan", "AC-harness-option-hold-4")]
    [Trait("Plan", "EDGE-harness-option-hold-11")]
    [InlineData("""{"mod":"Skirt","options":[]}""")]
    [InlineData("""{"mod":"Skirt","options":{}}""")]
    [InlineData("""{"mod":"Skirt","options":{"Extras":"Belt"}}""")]
    [InlineData("""{"mod":"Skirt","options":{"Extras":[1]}}""")]
    [InlineData("""{"mod":"Skirt","options":{"Extras":[""]}}""")]
    [InlineData("""{"mod":"Skirt","options":{"Extras":[["Belt"]]}}""")]
    [InlineData("""{"mod":"Skirt","options":{"":["Belt"]}}""")]
    public async Task Bad_options_shapes_400_nothing_held(string json)
    {
        var (status, body) = await fx.Post("/actions/mod-settings", json);
        Assert.Equal((400, "invalid-field"), (status, Code(body)));
        Assert.Empty(Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-4")]
    [Trait("Plan", "EDGE-harness-option-hold-12")]
    public async Task Options_with_permanent_400_nothing_written()
    {
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","permanent":true,"enabled":false,"options":{"Extras":["Bow"]}}""");
        Assert.Equal((400, "invalid-field"), (status, Code(body)));
        Assert.Empty(Game.Calls);
    }

    [Theory]
    [Trait("Plan", "AC-harness-option-hold-4")]
    [Trait("Plan", "EDGE-harness-option-hold-8")]
    [Trait("Plan", "EDGE-harness-option-hold-9")]
    [Trait("Plan", "EDGE-harness-option-hold-10")]
    [InlineData("""{"mod":"Skirt","options":{"Body — Skirt / Model(s)":[]}}""")]
    [InlineData("""{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Bibo S","Rue WC"]}}""")]
    [InlineData("""{"mod":"Skirt","options":{"Extras":["Belt","belt"]}}""")]
    [InlineData("""{"mod":"Skirt","options":{"Extras":["Belt"],"EXTRAS":["Bow"]}}""")]
    public async Task Counts_and_repeats_400_nothing_held(string json)
    {
        var (status, body) = await fx.Post("/actions/mod-settings", json);
        Assert.Equal((400, "invalid-field"), (status, Code(body)));
        Assert.Empty(Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-8")]
    public async Task A_multi_or_imc_group_may_select_none()
    {
        var (status, _) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":[],"Lace":[]}}""");
        Assert.Equal(200, status);
        Assert.Empty(Game.HoldSets.Single().Options["Extras"]);
        var (two, _) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Belt","Bow"]}}""");
        Assert.Equal(200, two);
        Assert.Equal(["Belt", "Bow"], Game.HoldSets.Last().Options["Extras"]);
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-3")]
    [Trait("Plan", "EDGE-harness-option-hold-2")]
    public async Task Wrong_case_resolves_in_the_mods_spelling()
    {
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"extras":["bow"]}}""");
        Assert.Equal(200, status);
        Assert.Equal(["Bow"], Game.HoldSets.Single().Options["Extras"]);
        Assert.Equal(["Bow"], Pairs(body.GetProperty("options"))["Extras"]);
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-3")]
    [Trait("Plan", "EDGE-harness-option-hold-4")]
    public async Task Unknown_group_or_option_404_names_what_there_is()
    {
        var (s1, b1) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Body - Skirt / Model(s)":["Rue WC"]}}""");
        Assert.Equal((404, "option-not-found"), (s1, Code(b1)));
        Assert.Contains("Extras", Message(b1));
        Assert.Contains(Size, Message(b1));
        var (s2, b2) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Rue M"]}}""");
        Assert.Equal((404, "option-not-found"), (s2, Code(b2)));
        Assert.Contains("no option 'Rue M' in group", Message(b2));
        Assert.Contains("Rue WC", Message(b2));
        Assert.Contains("Tre Medium", Message(b2));
        Assert.Empty(Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-5")]
    public async Task A_mod_with_no_groups_404()
    {
        fx.AddMod("Plain");
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Plain","options":{"Size":["S"]}}""");
        Assert.Equal((404, "option-not-found"), (status, Code(body)));
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-6")]
    [Trait("Plan", "EDGE-harness-option-hold-6")]
    public async Task Unreadable_groups_409_mod_unreadable()
    {
        File.WriteAllText(Path.Combine(fx.ModsRoot, "Skirt", "meta.json"), "{ not json");
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal((409, "mod-unreadable"), (status, Code(body)));
        Assert.Empty(Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-6")]
    [Trait("Plan", "EDGE-harness-option-hold-15")]
    public async Task Penumbra_refusing_the_selection_409_nothing_recorded()
    {
        Game.SetHoldAnswer = "OptionMissing";
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal((409, "penumbra-refused"), (status, Code(body)));
        Assert.Contains("OptionMissing", Message(body));
        var (_, state) = await fx.Get("/state");
        Assert.Equal(0, state.GetProperty("harnessChanges").GetProperty("holds").GetArrayLength());
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-5")]
    [Trait("Plan", "EDGE-harness-option-hold-13")]
    public async Task Options_alone_on_an_unconfigured_mod_sends_only_the_named_group_and_stays_off()
    {
        Game.Settings["Skirt"] = new(PenumbraBridge.SettingsReadKind.Ok, null);
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal(200, status);
        Assert.False(body.GetProperty("enabled").GetBoolean());
        var sent = Game.HoldSets.Single();
        Assert.False(sent.Enabled);
        Assert.Equal(["Extras"], sent.Options.Keys.ToArray());
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-22")]
    public async Task Inherited_settings_are_merged_and_held_here()
    {
        Game.Settings["Skirt"] = new(PenumbraBridge.SettingsReadKind.Ok,
            new(true, 7, new Dictionary<string, List<string>> { [Size] = ["Tre Medium"], ["Extras"] = ["Belt", "Bow"] }, true, false));
        var (status, _) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Rue WC"]}}""");
        Assert.Equal(200, status);
        var sent = Game.HoldSets.Single();
        Assert.Equal(Coll, sent.Collection);
        Assert.Equal(["Rue WC"], sent.Options[Size]);
        Assert.Equal(["Belt", "Bow"], sent.Options["Extras"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-16")]
    public async Task Held_elsewhere_409_no_set()
    {
        Game.Temporary[(Coll, "Skirt")] = new PenumbraBridge.TemporaryHold(true, false, "Glamourer", true, 7);
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal((409, "held-elsewhere"), (status, Code(body)));
        Assert.Empty(Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "AC-harness-option-hold-7")]
    public async Task A_hold_without_options_answers_the_options_it_kept()
    {
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","enabled":false}""");
        Assert.Equal(200, status);
        var echo = Pairs(body.GetProperty("options"));
        Assert.Equal(["Bibo S"], echo[Size]);
        Assert.Equal(["Belt"], echo["Extras"]);
        var (_, state) = await fx.Get("/state");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("harnessChanges").GetProperty("holds")[0].GetProperty("options").ValueKind);
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-17")]
    public async Task Options_while_busy_or_stopping_refused_nothing_held()
    {
        Assert.True(fx.Jobs.TryReserve());
        var (busy, busyBody) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal((409, "busy"), (busy, Code(busyBody)));
        Assert.Empty(Game.HoldSets);
        fx.Jobs.Release();
        fx.Changes.BeginStop();
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal((503, "harness-stopping"), (status, Code(body)));
        Assert.Empty(Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-18")]
    public async Task A_mod_removed_between_the_hops_404_nothing_held()
    {
        Game.OnMods = n => { if (n == 2) Game.ModFolders.Remove("Skirt"); };
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Extras":["Bow"]}}""");
        Assert.Equal((404, "mod-not-found"), (status, Code(body)));
        Assert.Empty(Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-19")]
    public async Task Options_in_another_collection_held_there_without_a_redraw_and_cleared()
    {
        var (status, body) = await fx.Post("/actions/mod-settings",
            $$$"""{"mod":"Skirt","collection":"{{{FakeGame.OtherCollection}}}","options":{"Extras":["Bow"]}}""");
        Assert.Equal(200, status);
        Assert.False(body.GetProperty("redrawRequested").GetBoolean());
        Assert.Equal(FakeGame.OtherCollection, Game.HoldSets.Single().Collection);
        await fx.Post("/actions/put-back", "{}");
        Assert.False(Game.Temporary.ContainsKey((FakeGame.OtherCollection, "Skirt")));
    }

    [Fact]
    [Trait("Plan", "EDGE-harness-option-hold-23")]
    public async Task Echoed_names_are_scrubbed_Penumbra_gets_the_real_ones()
    {
        // Two group names that scrub to the same placeholder stay two pairs (a dictionary would fold them into one).
        fx.Names.Add("Rue", NameKind.Collection);
        fx.Names.Add("Extras", NameKind.Collection);
        fx.Names.Add("Lace", NameKind.Collection);
        var (status, body) = await fx.Post("/actions/mod-settings",
            """{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Rue WC"],"Extras":["Bow"],"Lace":["A"]}}""");
        Assert.Equal(200, status);
        var (_, state) = await fx.Get("/state");
        foreach (var list in new[] { body.GetProperty("options"), state.GetProperty("harnessChanges").GetProperty("holds")[0].GetProperty("options") })
        {
            string raw = list.GetRawText();
            Assert.DoesNotContain("Rue", raw);
            Assert.DoesNotContain("Extras", raw);
            Assert.DoesNotContain("Lace", raw);
            var pairs = list.EnumerateArray()
                            .Select(p => (p.GetProperty("group").GetString()!, string.Join("|", p.GetProperty("options").EnumerateArray().Select(o => o.GetString()))))
                            .ToList();
            Assert.Contains((Size, "<COLLECTION> WC"), pairs);
            Assert.Contains(("<COLLECTION>", "Bow"), pairs);
            Assert.Contains(("<COLLECTION>", "A"), pairs);
        }
        var sent = Game.HoldSets.Single().Options;
        Assert.Equal(["Rue WC"], sent[Size]);
        Assert.Equal(["Bow"], sent["Extras"]);
        Assert.Equal(["A"], sent["Lace"]);
        var (_, b404) = await fx.Post("/actions/mod-settings", """{"mod":"Skirt","options":{"Body — Skirt / Model(s)":["Nope"]}}""");
        Assert.DoesNotContain("Rue", Message(b404));
    }
}
