using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What the Body size tool remembers between garments: a body mod per garment part, and the sizes per body mod (#36).
/// </summary>
public class RememberedTargetsTests
{
    private static Configuration WithSizes(string body = "Neolithe")
    {
        var c = new Configuration();
        RememberedTargets.ChoseSize(c, "_top", body, "_top", "chest/SFW L.mdl");
        RememberedTargets.ChoseSize(c, "_top", body, "_dwn", "legs/Gen C Large.mdl");
        RememberedTargets.ChoseSize(c, "_top", body, "_glv", "hands/Short nails.mdl");
        return c;
    }

    private static List<(string Slot, string Rel)> Fill(Configuration c, string? bodyDir = "Neolithe",
                                                        Func<string, bool>? isDrawn = null, Func<string, bool>? hasTarget = null)
        => RememberedTargets.Fillable(c, bodyDir, isDrawn ?? (_ => true), hasTarget ?? (_ => false)).ToList();

    private static Configuration RoundTrip(Configuration c)
    {
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects };
        return JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(c, settings), settings)!;
    }

    private static RememberedTargets.Restore Open(Configuration c, string? part, string? current,
                                                   string[]? installed = null, Func<string, bool>? usable = null)
        => RememberedTargets.BodyToOpen(c, part, current, installed ?? ["A", "B", "Neolithe"], usable ?? (_ => true));

    // ── sizes, per body ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-6")]
    public void Every_remembered_size_is_filled_in()   // [AC-gh33-36-37-body-size-6]
    {
        // One choice, remembered, applied to all of them rather than a dropdown each. Every part, paired or not: a part
        // joins the refit only once both its ends are chosen, so an unpaired size sits out until its source lands.
        var c = WithSizes();
        Assert.Equal(["_top", "_dwn", "_glv"], Fill(c).Select(f => f.Slot));
        Assert.Equal("legs/Gen C Large.mdl", Fill(c).Single(f => f.Slot == "_dwn").Rel);
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-6")]
    public void A_part_the_panel_does_not_draw_is_left_alone()   // [AC-gh33-36-37-body-size-6]
    {
        // No dropdown to show a filled-in size in and none to untick it from.
        Assert.Equal(["_top", "_dwn"], Fill(WithSizes(), isDrawn: slot => slot != "_glv").Select(f => f.Slot));
    }

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-4")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-14")]
    public void Fillable_skips_a_slot_with_its_own_body()   // [EDGE-gh33-36-37-body-size-14]
    {
        // The legs set to a body of their own, no size picked there yet: the remembered legs size is the main body's
        // file, and is never put into the other body's list.
        var to = new Dictionary<string, List<BodyOption>>(StringComparer.Ordinal);
        var own = new Dictionary<string, string>(StringComparer.Ordinal) { ["_dwn"] = "YAB+" };
        var filled = Fill(WithSizes(), hasTarget: slot => StudioPartBodies.HasChoice(slot, to, own));

        Assert.Equal(["_top", "_glv"], filled.Select(f => f.Slot));
        Assert.False(StudioPartBodies.Remembers("_dwn", own));
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-6")]
    public void A_choice_already_made_is_never_overwritten()   // [AC-gh33-36-37-body-size-6]
    {
        Assert.Equal(["_top", "_glv"], Fill(WithSizes(), hasTarget: slot => slot == "_dwn").Select(f => f.Slot));
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-6")]
    public void Sizes_of_another_body_mod_are_not_offered()   // [AC-gh33-36-37-body-size-6]
    {
        // Remembered by the option's file relative to its own mod. Against a different mod they are at best not found,
        // and at worst — two mods laying their files out alike — a size of this one nobody chose.
        var c = WithSizes("A");
        Assert.Empty(Fill(c, "B"));
        Assert.Empty(Fill(c, null));
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-6")]
    public void Picking_another_body_keeps_the_first_bodys_sizes()   // [AC-gh33-36-37-body-size-6]
    {
        var c = new Configuration();
        RememberedTargets.ChoseSize(c, "_top", "A", "_top", "chest/L.mdl");
        RememberedTargets.PickedBody(c, "_dwn", "B");
        RememberedTargets.ChoseSize(c, "_dwn", "B", "_dwn", "legs/M.mdl");

        foreach (var config in new[] { c, RoundTrip(c) })
        {
            Assert.Equal([("_top", "chest/L.mdl")], Fill(config, "A"));
            Assert.Equal([("_dwn", "legs/M.mdl")], Fill(config, "B"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-13")]
    public void Forgetting_a_size_touches_only_that_body()   // [EDGE-gh33-36-37-body-size-13]
    {
        var c = new Configuration();
        RememberedTargets.ChoseSize(c, "_top", "A", "_top", "a/top.mdl");
        RememberedTargets.ChoseSize(c, "_top", "A", "_dwn", "a/dwn.mdl");
        RememberedTargets.ChoseSize(c, "_dwn", "B", "_dwn", "b/dwn.mdl");

        RememberedTargets.ChoseSize(c, "_top", "A", "_dwn", null);
        Assert.Equal(["_top"], RememberedTargets.SizesOf(c, "A")!.Keys);
        Assert.Equal(["_dwn"], RememberedTargets.SizesOf(c, "B")!.Keys);

        RememberedTargets.ChoseSize(c, "_top", "A", "_top", null);
        Assert.Null(RememberedTargets.SizesOf(c, "A"));
        Assert.DoesNotContain("A", c.RetargetSizesByBody.Keys);
        Assert.Equal(["_dwn"], RememberedTargets.SizesOf(c, "B")!.Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-8")]
    public void Sizes_are_found_whatever_the_folders_case_after_a_reload()   // [EDGE-gh33-36-37-body-size-8]
    {
        var c = RoundTrip(WithSizes("Neolithe"));   // ordinal dictionaries, as deserialisation hands them back
        RememberedTargets.ChoseSize(c, "_top", "NEOLITHE", "_sho", "feet/Flat.mdl");

        Assert.Equal(["Neolithe"], c.RetargetSizesByBody.Keys);
        Assert.Equal(4, RememberedTargets.SizesOf(c, "neolithe")!.Count);

        // The installed casing comes back, so the panel's own lookups by folder find it.
        var r = RememberedTargets.BodyToOpen(c, "_top", "A", ["Neolithe", "A"], _ => true);
        Assert.Equal("NEOLITHE", c.RetargetBodyByPart["_top"]);
        Assert.Equal("Neolithe", r.Dir);
        Assert.Null(RememberedTargets.BodyToOpen(c, "_top", "neolithe", ["Neolithe"], _ => true).Dir);
    }

    // ── the body a garment opens on ────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-4")]
    public void A_body_picked_for_a_part_opens_the_next_garment_of_that_part()   // [AC-gh33-36-37-body-size-4]
    {
        var c = new Configuration();
        RememberedTargets.PickedBody(c, "_dwn", "B");

        Assert.Equal("B", c.RetargetBodyByPart["_dwn"]);
        Assert.Equal("B", c.RetargetBodyDir);
        Assert.Equal(new RememberedTargets.Restore(true, "B"), Open(c, "_dwn", "A"));
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-5")]
    public void Tops_and_bottoms_each_open_on_their_own_body()   // [AC-gh33-36-37-body-size-5]
    {
        // The #36 repro: chest from A, legs from B. Each pick once; afterwards no click.
        var c = new Configuration();
        RememberedTargets.PickedBody(c, "_top", "A");
        RememberedTargets.PickedBody(c, "_dwn", "B");

        string? current = "B";
        foreach (var (part, want) in new[] { ("_top", "A"), ("_dwn", "B"), ("_top", "A"), ("_dwn", "B") })
        {
            Assert.Equal(want, Open(c, part, current).Dir);
            current = want;
            Assert.Null(Open(c, part, current).Dir);   // already on it: nothing to change
        }
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-7")]
    public void A_part_with_no_body_remembered_keeps_the_body_chosen()   // [AC-gh33-36-37-body-size-7]
    {
        var c = new Configuration();
        RememberedTargets.PickedBody(c, "_top", "A");
        Assert.Equal(new RememberedTargets.Restore(true, null), Open(c, "_dwn", "B"));
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-7")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-12")]
    public void Before_any_body_is_chosen_the_last_picked_body_opens()   // [AC-gh33-36-37-body-size-7] [EDGE-gh33-36-37-body-size-12]
    {
        var c = new Configuration { RetargetBodyDir = "A" };
        Assert.Equal("A", Open(c, "_dwn", null).Dir);
        Assert.Null(Open(new Configuration(), "_dwn", null).Dir);   // nothing remembered: the worn guess runs
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-6")]
    public void A_remembered_body_no_longer_installed_is_skipped()   // [EDGE-gh33-36-37-body-size-6]
    {
        var c = new Configuration();
        RememberedTargets.PickedBody(c, "_top", "A");
        RememberedTargets.PickedBody(c, "_dwn", "B");   // the last body is B now
        c.RetargetBodyDir = "A";

        Assert.Null(Open(c, "_dwn", "C", installed: ["A", "C"]).Dir);              // uninstalled
        Assert.Null(Open(c, "_dwn", "C", usable: d => d != "B").Dir);              // no longer a body
        Assert.Equal("A", Open(c, "_dwn", null, usable: d => d != "B").Dir);       // the last body, with none chosen
        Assert.Null(Open(c, "_dwn", null, usable: _ => false).Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-7")]
    public void A_garment_of_no_body_part_uses_the_last_body()   // [EDGE-gh33-36-37-body-size-7]
    {
        var c = new Configuration();
        RememberedTargets.PickedBody(c, null, "A");
        Assert.Empty(c.RetargetBodyByPart);
        Assert.Equal("A", c.RetargetBodyDir);

        RememberedTargets.ChoseSize(c, null, "A", "_top", "chest/L.mdl");
        Assert.Equal("chest/L.mdl", RememberedTargets.SizesOf(c, "A")!["_top"]);
        Assert.Empty(c.RetargetBodyByPart);

        Assert.Equal("A", Open(c, null, null).Dir);
        Assert.Null(Open(c, null, "B").Dir);

        Assert.True(RememberedTargets.Sizes(["_top"], null, false));
        Assert.False(RememberedTargets.Sizes([], null, false));
        Assert.False(RememberedTargets.Sizes(null, null, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-15")]
    public void A_shoe_remembers_its_own_body()   // [EDGE-gh33-36-37-body-size-15]
    {
        var c = new Configuration();
        RememberedTargets.PickedBody(c, "_sho", "B");
        Assert.Equal("B", Open(c, "_sho", "A").Dir);
        Assert.Null(Open(c, "_dwn", "A").Dir);
        Assert.Null(Open(c, "_glv", "A").Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-16")]
    public void Opening_on_the_body_already_chosen_changes_nothing()   // [EDGE-gh33-36-37-body-size-16]
    {
        var c = WithSizes("A");
        RememberedTargets.PickedBody(c, "_dwn", "B");
        string before = JsonConvert.SerializeObject(c);

        string? current = Open(c, "_dwn", "A").Dir;
        Assert.Equal("B", current);
        for (int i = 0; i < 3; i++) Assert.Null(Open(c, "_dwn", current).Dir);
        Assert.Equal(before, JsonConvert.SerializeObject(c));
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-17")]
    public void Nothing_is_restored_before_the_body_mods_are_found()   // [EDGE-gh33-36-37-body-size-17]
    {
        var c = new Configuration { RetargetBodyDir = "A" };
        RememberedTargets.PickedBody(c, "_top", "A");
        Assert.Equal(new RememberedTargets.Restore(false, null),
                     RememberedTargets.BodyToOpen(c, "_top", null, null, _ => true));
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-19")]
    public void A_size_on_a_guessed_body_remembers_it_for_the_part()   // [EDGE-gh33-36-37-body-size-19]
    {
        var c = new Configuration();
        RememberedTargets.ChoseSize(c, "_top", "A", "_dwn", "legs/M.mdl");
        Assert.Equal("A", c.RetargetBodyByPart["_top"]);
        Assert.Equal("A", c.RetargetBodyDir);
        Assert.Equal(["_dwn"], RememberedTargets.SizesOf(c, "A")!.Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-21")]
    public void A_mens_top_does_not_open_on_a_womens_body()   // [EDGE-gh33-36-37-body-size-21]
    {
        // A player with characters of both sexes: tops remembered on a women's body mod. A men's top is not sized there,
        // so it stays where it is rather than opening on a body with nothing for it.
        string root = Path.Combine(Path.GetTempPath(), "proteus-women-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "files"));
            foreach (string f in new[] { "top_s", "top_l", "dwn_s", "dwn_l" })
                File.WriteAllBytes(Path.Combine(root, "files", f + ".mdl"), [0]);
            const string top = "chara/equipment/e0000/model/c0201e0000_top.mdl";
            const string dwn = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";
            File.WriteAllText(Path.Combine(root, "meta.json"),
                "{\"FileVersion\":4,\"Name\":\"W\",\"Author\":\"x\",\"DefaultData\":{\"Files\":{},\"Manipulations\":[]},\"Groups\":["
                + $"{{\"Name\":\"Chest\",\"Type\":\"Single\",\"Priority\":0,\"DefaultSettings\":0,\"Options\":["
                + $"{{\"Name\":\"S\",\"Files\":{{\"{top}\":\"files/top_s.mdl\"}},\"Manipulations\":[]}},"
                + $"{{\"Name\":\"L\",\"Files\":{{\"{top}\":\"files/top_l.mdl\"}},\"Manipulations\":[]}}]}},"
                + $"{{\"Name\":\"Legs\",\"Type\":\"Single\",\"Priority\":0,\"DefaultSettings\":0,\"Options\":["
                + $"{{\"Name\":\"S\",\"Files\":{{\"{dwn}\":\"files/dwn_s.mdl\"}},\"Manipulations\":[]}},"
                + $"{{\"Name\":\"L\",\"Files\":{{\"{dwn}\":\"files/dwn_l.mdl\"}},\"Manipulations\":[]}}]}}]}}");
            var cat = BodySizeCatalog.Read(root);
            Assert.True(cat.IsBody);

            var men = cat.SlotsFor("0101").ToList();
            var women = cat.SlotsFor("0201").ToList();
            Assert.False(RememberedTargets.Sizes(men, "_top", false));
            Assert.True(RememberedTargets.Sizes(women, "_top", false));

            var c = new Configuration();
            RememberedTargets.PickedBody(c, "_top", "W");
            Assert.Null(RememberedTargets.BodyToOpen(c, "_top", "M", ["W", "M"], d => d != "W" || RememberedTargets.Sizes(men, "_top", false)).Dir);
            Assert.Equal("W", RememberedTargets.BodyToOpen(c, "_top", "M", ["W", "M"], d => d != "W" || RememberedTargets.Sizes(women, "_top", false)).Dir);

            var last = new Configuration { RetargetBodyDir = "W" };
            Assert.Null(RememberedTargets.BodyToOpen(last, "_top", null, ["W"], _ => RememberedTargets.Sizes(men, "_top", false)).Dir);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
