using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.AutoRefitTargetsTests;

namespace Proteus.Tests;

/// <summary>
/// A body mod of its own for one part of a garment in the Studio's Body size tool (<see cref="StudioPartBodies"/>): a
/// long dress's legs onto another body than its chest. The panel is ImGui, which does not run headless; these are the
/// decisions it draws and plans by.
/// </summary>
public class StudioPartBodiesTests
{
    private static readonly RetargetInputs.Layout Whole = new("_top", "_top", null);
    private static readonly RetargetInputs.Layout KeptFeet = new("_dwn", "_sho", "_sho");

    private static Dictionary<string, string> Bodies(params (string Slot, string Dir)[] parts)
        => parts.ToDictionary(p => p.Slot, p => p.Dir, StringComparer.Ordinal);

    private static BodySizeCatalog? CatalogOf(string dir) => Find(dir)?.Catalog;

    // ── the picker ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-1")]
    [Trait("Plan", "EDGE-per-part-target-body-1")]
    public void Override_is_null_for_the_main_body_and_the_dir_otherwise()
    {
        Assert.Null(StudioPartBodies.Override(Bibo.Dir, Bibo.Dir));
        Assert.Null(StudioPartBodies.Override("Bibo+", "bibo+"));
        Assert.Equal(Yab.Dir, StudioPartBodies.Override(Bibo.Dir, Yab.Dir));
        Assert.Equal(Yab.Dir, StudioPartBodies.Override(null, Yab.Dir));
    }

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-1")]
    public void Resolve_gives_primary_the_main_body_and_a_part_its_own()
    {
        var bodies = Bodies(("_dwn", Yab.Dir), ("_top", Yab.Dir));
        Assert.Equal(new StudioPartBodies.Target(Bibo.Dir, Bibo.Catalog),
                     StudioPartBodies.Resolve("_top", "_top", Bibo.Dir, Bibo.Catalog, bodies, CatalogOf));
        Assert.Equal(new StudioPartBodies.Target(Yab.Dir, Yab.Catalog),
                     StudioPartBodies.Resolve("_dwn", "_top", Bibo.Dir, Bibo.Catalog, bodies, CatalogOf));
        Assert.Equal(new StudioPartBodies.Target(Bibo.Dir, Bibo.Catalog),
                     StudioPartBodies.Resolve("_glv", "_top", Bibo.Dir, Bibo.Catalog, bodies, CatalogOf));
    }

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-1")]
    [Trait("Plan", "EDGE-per-part-target-body-6")]
    [Trait("Plan", "EDGE-per-part-target-body-7")]
    public void Offered_only_for_the_whole_garment_without_a_picked_race()
    {
        Assert.True(StudioPartBodies.Offered(Whole, explicitRace: false));
        Assert.False(StudioPartBodies.Offered(KeptFeet, explicitRace: false));
        Assert.False(StudioPartBodies.Offered(Whole, explicitRace: true));
    }

    // ── what a refit is made from ───────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-2")]
    [Trait("Plan", "EDGE-per-part-target-body-8")]
    [Trait("Plan", "EDGE-per-part-target-body-19")]
    public void Across_when_a_part_goes_onto_another_body_than_made_for()
    {
        // No part bodies: exactly "a made-for body other than the refit-onto one, or a picked race".
        Assert.False(StudioPartBodies.Across("Bibo+", "Bibo+", ["Bibo+"], explicitRace: false));
        Assert.True(StudioPartBodies.Across("Bibo+", "YAB+", ["YAB+"], explicitRace: false));
        Assert.True(StudioPartBodies.Across("Bibo+", "Bibo+", [], explicitRace: true));
        Assert.True(StudioPartBodies.Across(VanillaBodyCatalog.Key, "Bibo+", [], explicitRace: false));
        // A part on a body of its own other than the made-for one.
        Assert.True(StudioPartBodies.Across("Bibo+", "Bibo+", ["YAB+"], explicitRace: false));
        Assert.True(StudioPartBodies.Across("Bibo+", "YAB+", ["Bibo+"], explicitRace: false));
    }

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-2")]
    public void Target_paths_lead_first_then_each_other_body_once()
    {
        Assert.Equal(["lead.mdl"], StudioPartBodies.TargetPaths("lead.mdl", "YAB+", [("YAB+", "yab-legs.mdl")]));
        Assert.Equal(["lead.mdl", "bibo-legs.mdl"],
                     StudioPartBodies.TargetPaths("lead.mdl", "YAB+",
                                                  [("Bibo+", "bibo-legs.mdl"), ("YAB+", "yab-hands.mdl"), ("bibo+", "BIBO-LEGS.mdl")]));
    }

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-2")]
    [Trait("Plan", "EDGE-per-part-target-body-19")]
    public void Option_name_names_each_body_run()
    {
        var mixed = StudioPartBodies.NamedParts(["_top", "_dwn"], ["M", "Gen A"], "A", s => s == "_dwn" ? "B" : "A");
        Assert.Equal("A — M + B — Gen A", RefitCore.OptionName(mixed));

        // One body: exactly the name it always had.
        var one = StudioPartBodies.NamedParts(["_top", "_dwn"], ["M", "L"], "A", _ => "A");
        Assert.Equal(RefitCore.OptionName("A", ["M", "L"]), RefitCore.OptionName(one));

        // A skin-only redraw has one label for no part: the main body's.
        var skin = StudioPartBodies.NamedParts(["_top", "_dwn"], [KeepEnds.SkinOnlyLabel], "A", _ => "B");
        Assert.Equal(RefitCore.OptionName("A", [KeepEnds.SkinOnlyLabel]), RefitCore.OptionName(skin));
    }

    // ── the hold ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-3")]
    [Trait("Plan", "EDGE-per-part-target-body-13")]
    public void Kept_on_own_body_when_source_is_main_and_a_part_goes_elsewhere()
    {
        Assert.True(StudioPartBodies.KeptOnOwnBody("X", "X", ["Y"]));
        Assert.True(StudioPartBodies.KeptOnOwnBody("x", "X", ["X", "Y"]));
        Assert.False(StudioPartBodies.KeptOnOwnBody("X", "Y", ["Z"]));
        Assert.False(StudioPartBodies.KeptOnOwnBody("X", "Y", ["X"]));   // the chest moves, the legs go to the made-for body
        Assert.False(StudioPartBodies.KeptOnOwnBody("X", "X", ["X"]));
        Assert.False(StudioPartBodies.KeptOnOwnBody("X", "X", []));
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-12")]
    public void Kept_on_own_body_counts_only_parts_taking_part()
    {
        // The legs have a body of their own but no "made for" size: they do not take part, so nothing is held.
        var stored = Bodies(("_dwn", "Y"));
        var taking = StudioPartBodies.TakingPart(["_top"], stored, offered: true);
        string? DirOf(string s) => stored.GetValueOrDefault(s) ?? "X";
        Assert.False(StudioPartBodies.KeptOnOwnBody("X", "X", taking.Where(s => s != "_top").Select(DirOf)));
        var both = StudioPartBodies.TakingPart(["_top", "_dwn"], stored, offered: true);
        Assert.True(StudioPartBodies.KeptOnOwnBody("X", "X", both.Where(s => s != "_top").Select(DirOf)));
    }

    // ── the Rue line ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-9")]
    [Trait("Plan", "EDGE-per-part-target-body-14")]
    [Trait("Plan", "EDGE-per-part-target-body-22")]
    public void RueApart_warns_across_the_line_only()
    {
        Assert.True(StudioPartBodies.RueApart(Rue.Dir, Rue.Name, Bibo.Dir, Bibo.Name));
        Assert.True(StudioPartBodies.RueApart(Bibo.Dir, Bibo.Name, Rue.Dir, Rue.Name));
        Assert.False(StudioPartBodies.RueApart(Rue.Dir, Rue.Name, LaRue.Dir, LaRue.Name));
        Assert.False(StudioPartBodies.RueApart(Bibo.Dir, Bibo.Name, Yab.Dir, Yab.Name));
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-22")]
    public void Rue_apart_never_holds_preview()
    {
        // Rue main, Bibo+ legs, made for another body: offered, and not held — only warned of.
        Assert.True(StudioPartBodies.Offered(Whole, explicitRace: false));
        Assert.False(StudioPartBodies.KeptOnOwnBody(Yab.Dir, Rue.Dir, [Bibo.Dir]));
    }

    // ── a part whose body has none of it ────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-2")]
    [Trait("Plan", "EDGE-per-part-target-body-3")]
    public void A_part_with_no_sizes_on_its_body_sits_out()
    {
        // LaRue has no hands; "Gone" is not installed.
        var bodies = Bodies(("_glv", LaRue.Dir), ("_sho", "Gone"));
        var hands = StudioPartBodies.Resolve("_glv", "_top", Bibo.Dir, Bibo.Catalog, bodies, CatalogOf);
        Assert.Empty(hands.Catalog!.For("_glv", "0201"));
        var feet = StudioPartBodies.Resolve("_sho", "_top", Bibo.Dir, Bibo.Catalog, bodies, CatalogOf);
        Assert.Equal("Gone", feet.Dir);
        Assert.Null(feet.Catalog);

        // With nothing to refit onto, the part has no "to" and sits out; the refit is still ready on the chest alone.
        var from = new Dictionary<string, BodyOption> { ["_top"] = Of(Bibo, "_top", "M"), ["_glv"] = Of(Bibo, "_glv", "M") };
        var to = new Dictionary<string, List<BodyOption>> { ["_top"] = [Of(Bibo, "_top", "L")] };
        Assert.Equal(["_top"], RetargetInputs.Chosen(["_top", "_glv"], from, to));
        var inputs = RetargetInputs.Assemble(Whole, ["_top", "_glv"], from, to, null);
        Assert.True(RetargetInputs.Ready(inputs, from, targetHasEnd: false, refused: false));
    }

    // ── two body mods laid out alike ────────────────────────────────────────

    private static readonly BodySizeCatalog RueFiles = new(Path.Combine("mods", "Rue"), [Size("_top", "M"), Size("_dwn", "M")]);
    private static readonly BodySizeCatalog LaRueFiles = new(Path.Combine("mods", "LaRue"), [Size("_top", "M"), Size("_dwn", "M")]);

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-4")]
    public void Resolve_takes_each_parts_path_from_its_own_root()
    {
        var bodies = Bodies(("_dwn", "LaRue"));
        BodySizeCatalog? OwnOf(string dir) => dir == "LaRue" ? LaRueFiles : null;
        var legs = StudioPartBodies.Resolve("_dwn", "_top", "Rue", RueFiles, bodies, OwnOf);
        var chest = StudioPartBodies.Resolve("_top", "_top", "Rue", RueFiles, bodies, OwnOf);
        var size = legs.Catalog!.For("_dwn")[0];
        Assert.Equal(RueFiles.For("_dwn")[0].Rel, size.Rel);   // the same file within each mod...
        Assert.StartsWith(Path.Combine("mods", "LaRue"), legs.Catalog.PathOf(size));   // ...read from the part's own
        Assert.StartsWith(Path.Combine("mods", "Rue"), chest.Catalog!.PathOf(chest.Catalog.For("_top")[0]));
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-4")]
    public void Same_rel_on_two_bodies_is_not_the_same_file()
    {
        var rue = RueFiles.For("_dwn")[0];
        var laRue = LaRueFiles.For("_dwn")[0];
        Assert.True(StudioPartBodies.SameSize("Rue", rue, "rue", rue));
        Assert.False(StudioPartBodies.SameSize("Rue", rue, "LaRue", laRue));
        Assert.False(StudioPartBodies.SameFile(RueFiles.PathOf(rue), LaRueFiles.PathOf(laRue)));
        Assert.True(StudioPartBodies.SameFile(RueFiles.PathOf(rue), RueFiles.PathOf(rue).ToUpperInvariant()));
    }

    // ── set aside, not lost ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-4")]
    [Trait("Plan", "EDGE-per-part-target-body-6")]
    [Trait("Plan", "EDGE-per-part-target-body-7")]
    public void Effective_hides_but_keeps_part_bodies()
    {
        var stored = Bodies(("_dwn", Yab.Dir));
        Assert.Empty(StudioPartBodies.Effective(stored, offered: false));
        Assert.Equal(stored, StudioPartBodies.Effective(stored, offered: true));
        Assert.Single(stored);   // never cleared by hiding
        Assert.True(StudioPartBodies.SetAside("_dwn", stored, offered: false));
        Assert.False(StudioPartBodies.SetAside("_dwn", stored, offered: true));
        Assert.False(StudioPartBodies.SetAside("_glv", stored, offered: false));
    }

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-4")]
    [Trait("Plan", "EDGE-per-part-target-body-23")]
    public void Hidden_part_bodies_leave_no_trace()
    {
        // The legs set to LaRue, sized there with a file Rue (the main body) has at the same place.
        var stored = Bodies(("_dwn", LaRue.Dir));
        var from = new Dictionary<string, BodyOption> { ["_top"] = Of(Rue, "_top", "M"), ["_dwn"] = Of(Rue, "_dwn", "M") };
        var to = new Dictionary<string, List<BodyOption>>
        {
            ["_top"] = [Of(Rue, "_top", "M")],
            ["_dwn"] = [Of(LaRue, "_dwn", "M")],
        };
        Assert.Equal(Of(Rue, "_dwn", "M").Rel, to["_dwn"][0].Rel);
        var looked = new List<string>();
        BodySizeCatalog? Recording(string dir) { looked.Add(dir); return CatalogOf(dir); }

        // Set aside: the legs sit out of everything a refit is made, keyed and named from.
        var chosen = RetargetInputs.Chosen(["_top", "_dwn"], from, to);
        var taking = StudioPartBodies.TakingPart(chosen, stored, offered: false);
        var active = StudioPartBodies.Active(to, stored, offered: false);
        Assert.Equal(["_top"], taking);
        Assert.False(active.ContainsKey("_dwn"));
        var effective = StudioPartBodies.Effective(stored, offered: false);
        Assert.Empty(effective);

        // ...exactly as the same refit with no legs body and the legs unchosen.
        var bare = new Dictionary<string, List<BodyOption>> { ["_top"] = [Of(Rue, "_top", "M")] };
        var hiddenInputs = RetargetInputs.Assemble(Whole, ["_top", "_dwn"], from, active, null);
        var bareInputs = RetargetInputs.Assemble(Whole, ["_top", "_dwn"], from, bare, null);
        Assert.Empty(hiddenInputs.Shared);
        Assert.Equal(Key(bareInputs, ["_top"], from, bare, null), Key(hiddenInputs, taking, from, active, effective));
        Assert.Equal(StudioPartBodies.NamedParts(["_top"], ["M"], "Rue", _ => "Rue"),
                     StudioPartBodies.NamedParts(taking, ["M"], "Rue", s => effective.TryGetValue(s, out var d) ? d : "Rue"));
        Assert.Equal(StudioPartBodies.Across(Rue.Dir, Rue.Dir, [], false),
                     StudioPartBodies.Across(Rue.Dir, Rue.Dir, taking.Skip(1).Select(s =>
                         StudioPartBodies.Resolve(s, "_top", Rue.Dir, Rue.Catalog, effective, Recording).Dir), false));
        Assert.Equal(StudioPartBodies.TargetPaths("lead", Rue.Dir, []),
                     StudioPartBodies.TargetPaths("lead", Rue.Dir, taking.Skip(1).Select(s => ((string?)Rue.Dir, s))));
        Assert.DoesNotContain(LaRue.Dir, looked);

        // Offered again: the legs come back with their LaRue size.
        Assert.Equal(["_top", "_dwn"], StudioPartBodies.TakingPart(chosen, stored, offered: true));
        Assert.Same(to["_dwn"], StudioPartBodies.Active(to, stored, offered: true)["_dwn"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-19")]
    public void Taking_part_is_unchanged_without_part_bodies()
    {
        var none = Bodies();
        Assert.Equal(["_top", "_dwn"], StudioPartBodies.TakingPart(["_top", "_dwn"], none, offered: false));
        var to = new Dictionary<string, List<BodyOption>> { ["_dwn"] = [Of(Bibo, "_dwn", "M")] };
        Assert.Same(to, StudioPartBodies.Active(to, none, offered: false));
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-5")]
    public void Sizes_at_race_read_each_part_from_its_stored_body()
    {
        // At the new race only the legs' own body still offers the legs' file; the main body has none there.
        var mainHere = new BodySizeCatalog("<main>", [Size("_top", "M")]);
        var stored = Bodies(("_dwn", LaRue.Dir));
        var chosen = new Dictionary<string, List<BodyOption>>
        {
            ["_top"] = [Of(Rue, "_top", "M")],
            ["_dwn"] = [Of(LaRue, "_dwn", "M")],
        };
        var kept = RetargetInputs.StillOffered(chosen, StudioPartBodies.SizesAtRace(null, "main", mainHere, stored, CatalogOf, "0201"));
        Assert.Equal(["_dwn", "_top"], kept.Keys.Order(StringComparer.Ordinal));
        // Judged against the main body alone, the legs would be dropped.
        Assert.False(RetargetInputs.StillOffered(chosen, s => mainHere.For(s, "0201")).ContainsKey("_dwn"));
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-9")]
    public void Leaving_a_part_out_drops_its_body()
    {
        var stored = Bodies(("_dwn", Yab.Dir), ("_glv", Nails.Dir));
        var left = StudioPartBodies.Without(stored, "_dwn");
        Assert.Equal(["_glv"], left.Keys);
        Assert.Equal(2, stored.Count);
    }

    private static string Key(RetargetInputs.Inputs inputs, IReadOnlyList<string> chosen, Dictionary<string, BodyOption> from,
                              IReadOnlyDictionary<string, List<BodyOption>> to, IReadOnlyDictionary<string, string>? bodies)
        => RetargetInputs.Key("m.mdl", null, inputs, chosen, from, to, [], null, true, true, false, partBodies: bodies);
}
