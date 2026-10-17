using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which body and size each part of a refit goes onto once a collection can choose a body per part
/// (<see cref="AutoRefitTargets"/>), and what the record, the option names and the put-back make of refits spanning more
/// than one body. With one body for every part, every answer must be the one the single-body refit gave.
/// </summary>
public class AutoRefitTargetsTests
{
    internal static string PathOf(string slot) => $"chara/equipment/e0000/model/c0201e0000_{slot.TrimStart('_')}.mdl";

    internal static BodyOption Size(string slot, string name, string section = "", string? rel = null)
        => new("Size", section, name, rel ?? $"{slot.TrimStart('_')}/{name.ToLowerInvariant()}.mdl", PathOf(slot), slot);

    internal static BodyModIndex.Entry BodyMod(string dir, params BodyOption[] options)
        => new(dir, dir, new BodySizeCatalog("<" + dir + ">", options));

    /// <summary>Bibo+: chest M and L, legs M and L, one pair of hands and of feet.</summary>
    internal static readonly BodyModIndex.Entry Bibo = BodyMod("Bibo+",
        Size("_top", "M"), Size("_top", "L"), Size("_dwn", "M"), Size("_dwn", "L"), Size("_glv", "M"), Size("_sho", "M"));

    /// <summary>YAB+: its own sizes, named apart from Bibo+'s but for M.</summary>
    internal static readonly BodyModIndex.Entry Yab = BodyMod("YAB+",
        Size("_top", "S"), Size("_top", "M"), Size("_dwn", "S"), Size("_dwn", "M"), Size("_glv", "Long"), Size("_glv", "M"),
        Size("_sho", "Flat"));

    /// <summary>A hands-only mod.</summary>
    internal static readonly BodyModIndex.Entry Nails = BodyMod("Nails", Size("_glv", "Claws"), Size("_glv", "Short"));

    /// <summary>Rue+, and a fork of it.</summary>
    internal static readonly BodyModIndex.Entry Rue = BodyMod("hs-Rue+-2.2.7-y0f",
        Size("_top", "M"), Size("_dwn", "M"), Size("_glv", "M"), Size("_sho", "M"));

    internal static readonly BodyModIndex.Entry LaRue = BodyMod("LavaBod+ LaRue", Size("_top", "M"), Size("_dwn", "M"));

    internal static BodyModIndex.Entry? Find(string dir)
        => new[] { Bibo, Yab, Nails, Rue, LaRue }.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase));

    internal static BodySizeRef Ref(BodyModIndex.Entry body, string slot, string name)
        => AutoRefitDecisions.RefOf(body.Catalog.For(slot).First(o => o.Name == name));

    private static AutoRefitPreference Legacy(string dir, BodySizeRef? chest, BodySizeRef? legs)
        => new() { LegacyBodyDir = dir, Chest = chest, Legs = legs };

    /// <summary>The single-body refit's size per part, as RunJob worked it out before each part had a body: the oracle.</summary>
    private static BodyOption? Before(BodySizeCatalog cat, AutoRefitPreference pref, string slot, string primary,
                                      IReadOnlyDictionary<string, BodyOption?> worn)
    {
        var chestTo = AutoRefitDecisions.Resolve(cat.For("_top", "0201"), pref.Chest);
        var legsTo = AutoRefitDecisions.Resolve(cat.For("_dwn", "0201"), pref.Legs);
        BodyOption? Follow(string s) => (chestTo ?? legsTo) is { } anchor
            ? AutoRefitDecisions.Companion(cat.For(s, "0201"), anchor, worn.GetValueOrDefault(s))
            : null;
        return slot switch
        {
            "_top" => chestTo ?? (slot == primary ? null : Follow(slot)),
            "_dwn" => legsTo ?? (slot == primary ? null : Follow(slot)),
            _ => Follow(slot),
        };
    }

    // ── one body: exactly as before ──────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-5")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-16")]
    public void A_single_body_pref_resolves_exactly_as_before()
    {
        var worn = new Dictionary<string, BodyOption?> { ["_glv"] = Bibo.Catalog.For("_glv")[0] };
        foreach (var pref in new[]
                 {
                     Legacy(Bibo.Dir, Ref(Bibo, "_top", "L"), null),
                     Legacy(Bibo.Dir, null, Ref(Bibo, "_dwn", "M")),
                     Legacy(Bibo.Dir, Ref(Bibo, "_top", "M"), Ref(Bibo, "_dwn", "L")),
                     Legacy(Bibo.Dir, null, null),
                 })
        {
            var targets = new AutoRefitTargets(pref, Find, "0201", worn);
            foreach (string primary in AutoRefitDecisions.Slots)
                foreach (string slot in AutoRefitDecisions.Slots)
                    Assert.Equal(Before(Bibo.Catalog, pref, slot, primary, worn), targets.TargetFor(slot, primary));
            Assert.All(AutoRefitDecisions.Slots, s => Assert.Equal(Bibo.Dir, targets.DirOf(s)));
        }
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-5")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-28")]
    public void A_single_body_config_maps_its_body_to_every_part_and_keeps_its_key()
    {
        // As an earlier build saved it.
        const string old = """{ "CollectionName": "c", "BodyDir": "Rue", "Chest": { "Rel": "chest/m.mdl" } }""";
        var pref = JsonConvert.DeserializeObject<AutoRefitPreference>(old)!;
        Assert.All(AutoRefitDecisions.Slots, s => Assert.Equal("Rue", pref.BodyOf(s)));
        Assert.True(pref.HasAnyBody());
        Assert.Null(pref.SizeOf("_glv"));

        // Saved again, the key an older build reads is still there; after a per-part edit, it is the chest's body.
        Assert.Contains("\"BodyDir\":\"Rue\"", JsonConvert.SerializeObject(pref));
        pref.SetBody("_dwn", "YAB+");
        Assert.Equal("Rue", JsonConvert.DeserializeObject<AutoRefitPreference>(JsonConvert.SerializeObject(pref))!.LegacyBodyDir);
        pref.SetBody("_top", "Bibo+");
        Assert.Contains("\"BodyDir\":\"Bibo+\"", JsonConvert.SerializeObject(pref));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-5")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-16")]
    public void One_body_option_name_cover_key_and_record_are_unchanged()
    {
        Assert.Equal(RefitCore.OptionName("Bibo+", ["M", "L"]), RefitCore.OptionName([("Bibo+", "M"), ("Bibo+", "L")]));
        Assert.Equal("Bibo+ — M", RefitCore.OptionName([("Bibo+", "M")]));

        byte[] bytes = [1, 2, 3];
        var pref = Legacy(Bibo.Dir, Ref(Bibo, "_top", "M"), Ref(Bibo, "_dwn", "L"));
        Assert.Equal(AccessoryCover.Key(bytes, Bibo.Dir, "top/m.mdl", "dwn/l.mdl", "0201"),
                     AccessoryCover.Key(bytes, AutoRefitTargets.ChosenOf(pref), "0201"));

        Assert.Null(AutoRefitTargets.BodiesOf(new Dictionary<string, string> { ["_top"] = "Bibo+", ["_dwn"] = "bibo+" }, "Bibo+"));
    }

    // ── a body per part ──────────────────────────────────────────────────────

    private static AutoRefitPreference Mixed(BodySizeRef? chest = null, BodySizeRef? legs = null, BodySizeRef? hands = null)
    {
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", Bibo.Dir);
        pref.SetBody("_dwn", Yab.Dir);
        pref.SetBody("_glv", Yab.Dir);
        pref.SetBody("_sho", Bibo.Dir);
        pref.Chest = chest;
        pref.Legs = legs;
        pref.Hands = hands;
        return pref;
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-6")]
    public void Each_part_resolves_in_its_own_body()
    {
        var targets = new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "L"), Ref(Yab, "_dwn", "S")), Find, "0201");
        Assert.Equal("L", targets.TargetFor("_top", "_top")!.Name);
        Assert.Equal("S", targets.TargetFor("_dwn", "_top")!.Name);
        Assert.StartsWith("<YAB+>", targets.PathOf("_dwn", targets.TargetFor("_dwn", "_top")!), StringComparison.Ordinal);
        Assert.StartsWith("<Bibo+>", targets.PathOf("_top", targets.TargetFor("_top", "_top")!), StringComparison.Ordinal);
        Assert.Equal("Bibo+", targets.NameOf("_top"));
        Assert.Equal("YAB+", targets.NameOf("_dwn"));
        Assert.Equal("Bibo+ — L + YAB+ — S", RefitCore.OptionName([("Bibo+", "L"), ("YAB+", "S")]));
        Assert.Equal("Bibo+ — L + M + YAB+ — S", RefitCore.OptionName([("Bibo+", "L"), ("Bibo+", "M"), ("YAB+", "S")]));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-6")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-35")]
    public void Across_when_any_pair_dir_differs_from_the_source()
    {
        Assert.False(AutoRefitTargets.Across("Bibo+", ["Bibo+", "bibo+"]));
        // The garment's own part already on its file, a part it reaches going elsewhere: between bodies all the same,
        // so that part is paired even though the garment's own is where it was.
        Assert.True(AutoRefitTargets.Across("Bibo+", ["Bibo+", "YAB+"]));
        Assert.True(AutoRefitTargets.Across("Bibo+", ["YAB+"]));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-3")]
    public void An_unchosen_primary_is_refused()
    {
        var targets = new AutoRefitTargets(Mixed(legs: Ref(Yab, "_dwn", "S")), Find, "0201");
        Assert.Null(targets.TargetFor("_top", "_top"));
        Assert.NotNull(targets.TargetFor("_dwn", "_dwn"));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-9")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-12")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-13")]
    public void A_missing_body_gives_no_target_for_its_part_and_names_it()
    {
        var pref = Mixed(Ref(Bibo, "_top", "M"), Ref(Yab, "_dwn", "S"));
        pref.SetBody("_dwn", "Gone Body");
        var targets = new AutoRefitTargets(pref, Find, "0201");
        Assert.Equal("Gone Body", targets.MissingDir("_dwn"));
        Assert.Null(targets.MissingDir("_top"));
        Assert.Null(targets.TargetFor("_dwn", "_top"));       // a reached part with its body gone: left out
        Assert.NotNull(targets.TargetFor("_top", "_top"));
        // A garment whose own part's body is gone: that body is the one named.
        Assert.Equal("_dwn", AutoRefitTargets.LeadSlot("_sho"));
        Assert.Equal("Gone Body", targets.MissingDir(AutoRefitTargets.LeadSlot("_sho")));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-23")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-25")]
    public void Lead_body_of_gloves_is_the_chest_body_and_its_races_route_them()
    {
        var targets = new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "M")), Find, "0201");
        Assert.Equal("_top", AutoRefitTargets.LeadSlot("_glv"));
        Assert.Equal("Bibo+", targets.Body(AutoRefitTargets.LeadSlot("_glv"))!.Dir);
        Assert.Equal(RefitCore.RacesOf(Bibo.Catalog), RefitCore.RacesOf(targets.Body(AutoRefitTargets.LeadSlot("_glv"))!.Catalog));
        // A part with no sizes at the race resolves to nothing.
        Assert.Null(new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "M")), Find, "0101").TargetFor("_dwn", "_top"));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-10")]
    public void Matched_hands_follow_a_chest_change_explicit_hands_do_not()
    {
        var m = new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "M")), Find, "0201");
        Assert.Equal("M", m.TargetFor("_glv", "_glv")!.Name);              // YAB+ has an M: matched by name
        var l = new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "L")), Find, "0201");
        Assert.Equal("Long", l.TargetFor("_glv", "_glv")!.Name);           // no L: the first
        var held = new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "L"), hands: Ref(Yab, "_glv", "M")), Find, "0201");
        Assert.Equal("M", held.TargetFor("_glv", "_glv")!.Name);           // chosen: stays
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-11")]
    public void Matched_across_bodies_falls_to_worn_then_first_on_both_sides()
    {
        var options = Yab.Catalog.For("_glv", "0201");
        var anchor = Bibo.Catalog.For("_top").First(o => o.Name == "L");
        var worn = options.First(o => o.Name == "M");
        Assert.Equal(worn, AutoRefitTargets.Matched(options, anchor, worn, out var basis));
        Assert.Equal(AutoRefitTargets.Basis.Worn, basis);
        Assert.Equal(options[0], AutoRefitTargets.Matched(options, anchor, null, out basis));
        Assert.Equal(AutoRefitTargets.Basis.First, basis);
        // The refit asks the same function, with what is worn handed in.
        var targets = new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "L")), Find, "0201",
                                           new Dictionary<string, BodyOption?> { ["_glv"] = worn });
        Assert.Equal(worn, targets.TargetFor("_glv", "_glv"));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-32")]
    public void A_reached_part_on_another_body_without_a_size_is_left_out_not_guessed()
    {
        var targets = new AutoRefitTargets(Mixed(Ref(Bibo, "_top", "M")), Find, "0201");
        Assert.Null(targets.TargetFor("_dwn", "_top"));
        // On the chest's own body it follows the chest, as it always did.
        var one = new AutoRefitTargets(Legacy(Bibo.Dir, Ref(Bibo, "_top", "M"), null), Find, "0201");
        Assert.Equal("M", one.TargetFor("_dwn", "_top")!.Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-24")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-33")]
    public void Chest_beneath_targets_the_chest_body_and_never_crosses_the_legs()
    {
        // Wanted when the chest's body is another than the legs garment was made on, even with the legs in their mod.
        var chest = Bibo.Catalog.For("_top")[0];
        Assert.True(RefitCore.WantsChestBeneath("_dwn", [], chest, acrossBodies: true));
        Assert.False(RefitCore.WantsChestBeneath("_dwn", ["_top"], chest, acrossBodies: true));
        Assert.False(RefitCore.WantsChestBeneath("_dwn", [], null, acrossBodies: true));
        // It is not one of the garment's own parts: a refit between bodies is judged without it.
        Assert.False(AutoRefitTargets.Across("YAB+", ["YAB+"]));
        Assert.False(RefitCore.IsOwnPart(RefitCore.ChestBeneathKey));
        Assert.False(RefitCore.IsOwnPart(RefitCore.EndKey("_glv")));
        Assert.True(RefitCore.IsOwnPart("_dwn"));
    }

    // ── Rue goes only with Rue ───────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-6")]
    public void Rue_is_told_by_its_name_and_a_part_across_the_line_is_left_out()
    {
        Assert.True(AutoRefitTargets.IsRue("hs-Rue+-2.2.7-y0f", "Rue+"));
        Assert.True(AutoRefitTargets.IsRue("LavaBod+ LaRue", null));
        Assert.False(AutoRefitTargets.IsRue("Bibo+", "Bibo+"));
        Assert.False(AutoRefitTargets.IsRue("Butt Plug - True Colors", null));

        var pref = new AutoRefitPreference { ChestBody = Rue.Dir, LegsBody = Bibo.Dir, Chest = Ref(Rue, "_top", "M"),
                                             Legs = Ref(Bibo, "_dwn", "M") };
        var targets = new AutoRefitTargets(pref, Find, "0201");
        Assert.Null(targets.TargetFor("_dwn", "_top"));
        Assert.Null(targets.TargetFor("_top", "_dwn"));
        // Rue and its fork go together.
        pref.LegsBody = LaRue.Dir;
        pref.Legs = Ref(LaRue, "_dwn", "M");
        Assert.NotNull(new AutoRefitTargets(pref, Find, "0201").TargetFor("_dwn", "_top"));
    }

    // ── the record and the put-back ──────────────────────────────────────────

    private static string NewMod()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-partbodies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Dress", "Someone", ""));
        return root;
    }

    private static void Save(string root, string gamePath, string option, string bodyMod,
                             Dictionary<string, string> targets, Dictionary<string, string>? bodies)
        => Assert.True(BodyRetargetWriter.Save(root, "Size", gamePath, bodyMod, "Bibo+ — M",
                                               [new BodyRetargetWriter.Refit(option, [1, 2, 3], "M")], auto: true,
                                               provenance: new BodyRetargetWriter.Provenance("Bibo+", "top/m.mdl", targets,
                                                                                            bodies)).Ok);

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-16")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-14")]
    public void Single_body_save_writes_no_target_bodies_and_a_mixed_one_records_each()
    {
        string root = NewMod();
        try
        {
            Save(root, PathOf("_top"), "Bibo+ — M", "Bibo+", new() { ["_top"] = "top/m.mdl", ["_dwn"] = "dwn/m.mdl" }, null);
            string json = File.ReadAllText(Directory.GetFiles(root, "bodyretarget.json", SearchOption.AllDirectories).Single());
            Assert.DoesNotContain("TargetBodies", json);
            var plain = BodyRetargetWriter.ReadRecord(root)!.Options.Single();
            Assert.Equal("Bibo+", plain.BodyOf("_dwn"));

            Save(root, PathOf("_top"), "Bibo+ — M + YAB+ — S", "Bibo+", new() { ["_top"] = "top/m.mdl", ["_dwn"] = "dwn/s.mdl" },
                 AutoRefitTargets.BodiesOf(new Dictionary<string, string> { ["_top"] = "Bibo+", ["_dwn"] = "YAB+" }, "Bibo+"));
            var mixed = BodyRetargetWriter.ReadRecord(root)!.Options.Single(e => e.Name.Contains("YAB+"));
            Assert.Equal("YAB+", mixed.BodyOf("_dwn"));
            Assert.Equal("Bibo+", mixed.BodyOf("_top"));
            // Put back on with the same name and source; with the legs on another body the name differs.
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M + YAB+ — S", PathOf("_top"), ("Bibo+", "top/m.mdl")));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M + Bibo+ — S", PathOf("_top"), ("Bibo+", "top/m.mdl")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-15")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-22")]
    public void Join_and_put_back_compare_each_parts_body_not_just_its_file()
    {
        var record = new BodyRetargetWriter.Record();
        record.Options.Add(new BodyRetargetWriter.Entry
        {
            Group = "Size", Name = "Rue — M + M", GamePath = PathOf("_dwn"), BodyMod = "Rue", Auto = true,
            Targets = new() { ["_top"] = "top/m.mdl", ["_dwn"] = "dwn/m.mdl", [RefitCore.EndKey("_glv")] = "x.mdl" },
        });
        var own = new Dictionary<string, string> { ["_top"] = "top/m.mdl" };
        var onRue = new Dictionary<string, (string Dir, string Rel)> { ["_top"] = ("Rue", "top/m.mdl"), ["_dwn"] = ("Rue", "dwn/m.mdl") };
        Assert.Equal("Rue — M + M", RefitCore.JoinOption(record, "Size", PathOf("_top"), onRue, own, "proposed"));
        // A fork naming its files as Rue does: the same file on another body is no join.
        var onFork = new Dictionary<string, (string Dir, string Rel)> { ["_top"] = ("LaRue", "top/m.mdl"), ["_dwn"] = ("Rue", "dwn/m.mdl") };
        Assert.Equal("proposed", RefitCore.JoinOption(record, "Size", PathOf("_top"), onFork, own, "proposed"));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-7")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-22")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-15")]
    public void A_glove_or_legs_refit_onto_other_hands_or_another_chest_body_is_made_again()
    {
        string root = NewMod();
        try
        {
            string glove = PathOf("_glv");
            // Plain matched on the arms' body: no end key, as every refit before.
            Save(root, glove, "Bibo+ — M", "Bibo+", new() { ["_top"] = "top/m.mdl" }, null);
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M", glove));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M", glove, end: ("end:_glv", "Nails", "glv/claws.mdl")));

            // Hands of their own: remade when their size or body changes.
            Save(root, glove, "Bibo+ — M", "Bibo+", new() { ["_top"] = "top/m.mdl", ["end:_glv"] = "glv/claws.mdl" },
                 new() { ["_top"] = "Bibo+", ["end:_glv"] = "Nails" });
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M", glove, end: ("end:_glv", "Nails", "glv/claws.mdl")));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M", glove, end: ("end:_glv", "Nails", "glv/short.mdl")));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M", glove, end: ("end:_glv", "YAB+", "glv/claws.mdl")));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Bibo+ — M", glove));

            // A legs refit's chest beneath, by body as well as file.
            string legs = PathOf("_dwn");
            Save(root, legs, "Rue — M", "Rue", new() { ["_dwn"] = "dwn/m.mdl", [RefitCore.ChestBeneathKey] = "top/m.mdl" }, null);
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue — M", legs, chestBeneath: "top/m.mdl", chestBeneathDir: "Rue"));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Rue — M", legs, chestBeneath: "top/m.mdl",
                                                   chestBeneathDir: "LavaBod+ LaRue"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-26")]
    public void Pair_option_names_both_bodies()
    {
        List<PenumbraModMeta.Redirect> redirects =
        [
            new(PathOf("_top"), "a/top.mdl", "Body Options / A"),
            new(PathOf("_dwn"), "a/dwn.mdl", "Body Options / A"),
        ];
        var chest = Bibo.Catalog.For("_top")[0];
        var legs = Yab.Catalog.For("_dwn")[0];
        var plan = RefitCore.PlanOption(redirects, null, "Body Options", true, ["A"], PathOf("_top"), "a/top.mdl",
                                        slot => slot == "_top" ? "Bibo+" : "YAB+", _ => true,
                                        slot => slot == "_top" ? chest : legs);
        Assert.True(plan.Paired);
        Assert.Equal("Bibo+ — M + YAB+ — S", plan.Option);
        var one = RefitCore.PlanOption(redirects, null, "Body Options", true, ["A"], PathOf("_top"), "a/top.mdl", "Bibo+",
                                       _ => true, slot => slot == "_top" ? chest : Bibo.Catalog.For("_dwn")[0]);
        Assert.Equal("Bibo+ — M + M", one.Option);
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-8")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-34")]
    public void Cover_key_and_beneath_use_each_parts_body()
    {
        byte[] bytes = [4, 5, 6];
        var mixed = Mixed(Ref(Bibo, "_top", "M"), Ref(Yab, "_dwn", "S"));
        var chosen = AutoRefitTargets.ChosenOf(mixed);
        Assert.Equal("Bibo+", chosen.ChestDir);
        Assert.Equal("YAB+", chosen.LegsDir);
        Assert.NotEqual(AccessoryCover.Key(bytes, "Bibo+", chosen.ChestRel, chosen.LegsRel, "0201"),
                        AccessoryCover.Key(bytes, chosen, "0201"));
        // The walk and the refit build it from one constructor: one key.
        Assert.Equal(AccessoryCover.Key(bytes, AutoRefitTargets.ChosenOf(mixed), "0201"),
                     AccessoryCover.Key(bytes, AutoRefitTargets.ChosenOf(mixed.Copy()), "0201"));

        var record = new BodyRetargetWriter.Entry
        {
            BodyMod = "Bibo+", SourceDir = "Bibo+", SourceRel = "dwn/m.mdl",
            Targets = new() { ["_top"] = "top/m.mdl", ["_dwn"] = "dwn/s.mdl" },
            TargetBodies = new() { ["_top"] = "Bibo+", ["_dwn"] = "YAB+" },
        };
        var pick = new AutoRefitDecisions.Pick("Bibo+", Bibo.Catalog.For("_dwn")[0], BodySizeMatch.Confidence.Exact, false);
        Assert.NotNull(AccessoryCover.FromBeneath(false, record, "YAB+", "_dwn", "dwn/s.mdl", null, () => (null, false),
                                                  (_, _) => pick).Source);
        Assert.Equal(AccessoryCover.Refusal.NotOnTarget,
                     AccessoryCover.FromBeneath(false, record, "Bibo+", "_dwn", "dwn/s.mdl", null, () => (null, false),
                                                (_, _) => pick).Why);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-19")]
    public void SkinLikeBody_keeps_every_bodys_skin()
    {
        const string bibo = "/mt_c0201b0001_bibo.mtrl", gen2 = "/mt_c0201b0001_a.mtrl", gen3 = "/mt_c0201b0001_b.mtrl";
        static byte[] Model(params string[] materials)
            => SyntheticModel.Build([], materials.Select(m => new SyntheticModel.Mesh(m, new SyntheticModel.Sub(0))).ToArray());
        var garment = Model(bibo, gen2, gen3);
        var chest = Model(bibo);
        var legs = Model(gen2);

        RacialModelBake.SkinLikeBody(garment, [chest, legs], out var both);
        Assert.Equal([gen3], both);                 // only the garment's own skin takes the lead's
        RacialModelBake.SkinLikeBody(garment, [chest], out var leadOnly);
        Assert.Equal([gen2, gen3], leadOnly.Order(StringComparer.Ordinal));
        // One body: as the single-body overload always did.
        RacialModelBake.SkinLikeBody(garment, chest, out var single);
        Assert.Equal(leadOnly.Order(StringComparer.Ordinal), single.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-34")]
    public void LegacyBodyDir_is_read_only_through_BodyOf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var readers = Directory.GetFiles(Path.Combine(dir!.FullName, "Proteus"), "*.cs", SearchOption.AllDirectories)
                               .Where(f => !f.EndsWith("Configuration.cs", StringComparison.Ordinal)
                                           && !f.EndsWith("AutoRefitDecisions.cs", StringComparison.Ordinal)
                                           && File.ReadAllText(f).Contains("LegacyBodyDir", StringComparison.Ordinal))
                               .ToList();
        // AutoRefitDecisions only clears it with the other parts, when a pick switches every part.
        Assert.Empty(readers);
    }
}
