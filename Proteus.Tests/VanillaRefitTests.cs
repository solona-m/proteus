using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Refitting the game's own gear: reading the game's body as a catalog, naming the mod a refit goes into, and
/// writing into a mod Proteus made rather than an author's. Over real temp folders, because what is being tested is
/// the shape of what lands on disk.
/// </summary>
public class VanillaRefitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-vanilla-" + Guid.NewGuid().ToString("N"));

    public VanillaRefitTests()
    {
        Directory.CreateDirectory(root);
        VanillaBodyCatalog.CleanUp();
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        VanillaBodyCatalog.CleanUp();
        GC.SuppressFinalize(this);
    }

    // ── the game's body as a catalog ───────────────────────────────────────────────────────────────

    /// <summary>A reader standing in for the game's own data, counting what it was asked for.</summary>
    private static Func<string, byte[]?> Game(List<string> asked, params string[] has)
        => path =>
        {
            asked.Add(path);
            return has.Contains(path, StringComparer.OrdinalIgnoreCase)
                ? SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201b0001_a.mtrl",
                                                                   new SyntheticModel.Sub(0, TrianglesPerIsland: 2)))
                : null;
        };

    private static string[] EverySlot(string race) =>
    [
        VanillaBodyCatalog.GamePathOf(race, "_top"), VanillaBodyCatalog.GamePathOf(race, "_dwn"),
        VanillaBodyCatalog.GamePathOf(race, "_glv"), VanillaBodyCatalog.GamePathOf(race, "_sho"),
    ];

    [Fact]
    public void The_game_s_body_reads_as_a_catalog_of_four_slots()
    {
        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0201", Game(asked, EverySlot("0201")));

        Assert.True(catalog.IsBody);
        Assert.Equal(["_top", "_dwn", "_glv", "_sho"], catalog.Slots);
        foreach (var option in catalog.Options)
        {
            // The same game path a body mod would replace, and a file the refit can actually open.
            Assert.True(BodySizeCatalog.IsBodyModel(option.GamePath), option.GamePath);
            Assert.Equal(option.Slot, BodySizeCatalog.SlotOf(option.GamePath));
            Assert.True(File.Exists(catalog.PathOf(option)), catalog.PathOf(option));
        }
        Assert.Equal("0201", BodySizeCatalog.RaceOf(catalog.Options[0].GamePath));
    }

    [Fact]
    public void The_game_is_read_once_a_session_not_once_a_call()
    {
        // Every pass of the refit — detect, validate, plan — asks for the body again, and each would otherwise be
        // another four reads out of the game's data on another thread.
        var asked = new List<string>();
        var read = Game(asked, EverySlot("0201"));

        VanillaBodyCatalog.Read("0201", read);
        VanillaBodyCatalog.Read("0201", read);
        VanillaBodyCatalog.Read("0201", read);

        Assert.Equal(4, asked.Count);
    }

    [Fact]
    public void A_race_the_game_has_no_body_for_is_not_a_body()
    {
        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("9901", Game(asked));

        Assert.False(catalog.IsBody);
        Assert.Empty(catalog.Options);
    }

    [Fact]
    public void A_slot_the_game_is_missing_is_left_out_and_the_rest_stand()
    {
        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0201", Game(asked, VanillaBodyCatalog.GamePathOf("0201", "_top"),
                                                                  VanillaBodyCatalog.GamePathOf("0201", "_dwn")));

        Assert.Equal(["_top", "_dwn"], catalog.Slots);
        Assert.True(catalog.IsBody);
    }

    [Fact]
    public void The_game_s_body_answers_the_race_rules_a_body_mod_does()
    {
        // The same fallback BodyRaceTests asserts for a mod: exact race, else the same sex, never across it.
        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0201", Game(asked, EverySlot("0201")));

        Assert.Single(catalog.For("_top", "0201"));
        Assert.Single(catalog.For("_top", "0401"));    // another women's race falls back
        Assert.Empty(catalog.For("_top", "0101"));     // a man's does not
    }

    // ── naming the mod a refit goes into ───────────────────────────────────────────────────────────

    [Fact]
    public void A_refit_mod_is_named_for_the_item_and_the_body()
    {
        Assert.Equal("Ala Mhigan Coat — Neolithe", RefitModService.NameFor("Ala Mhigan Coat", "Neolithe"));
        Assert.Equal("Ala Mhigan Coat Neolithe", RefitModService.DirFor("Ala Mhigan Coat — Neolithe"));
        Assert.Null(RefitModService.DirFor("———"));                // nothing usable in it
        Assert.Null(RefitModService.DirFor("Proteus"));            // reserved for the compositor's own mod
    }

    [Fact]
    public void A_folder_that_sanitises_the_same_but_holds_another_item_is_not_reused()
    {
        // "Storm Private's Coat" and "Storm Privates Coat" sanitise alike. Merging two garments into one mod would
        // be silent and wrong, so the manifest's own name decides, not the folder's.
        string mine = RefitModService.NameFor("Storm Private's Coat", "Neolithe");
        string theirs = RefitModService.NameFor("Storm Privates Coat", "Neolithe");
        Assert.Equal(RefitModService.DirFor(mine), RefitModService.DirFor(theirs));

        string taken = Path.Combine(root, RefitModService.DirFor(theirs)!);
        Directory.CreateDirectory(taken);
        RefitModService.WriteScaffold(taken, theirs, "");

        Assert.Null(RefitModService.Find(root, "Storm Private's Coat", "Neolithe"));
        Assert.Equal(taken, RefitModService.Find(root, "Storm Privates Coat", "Neolithe"));
    }

    [Fact]
    public void An_item_whose_mod_had_to_be_numbered_is_found_again()
    {
        // The second of two items that sanitise alike gets a numbered mod, and its manifest carries the numbered
        // name. Looking for it by the bare name finds it never — and then every save makes another mod, "(2)",
        // "(3)", with the refits scattered behind them.
        string first = RefitModService.NameFor("Storm Privates Coat", "Neolithe");
        string mine = RefitModService.NameFor("Storm Private's Coat", "Neolithe");

        string theirs = Path.Combine(root, RefitModService.DirFor(first)!);
        Directory.CreateDirectory(theirs);
        RefitModService.WriteScaffold(theirs, first, "");

        // What Ensure would do for the second item: the next free folder, under the numbered name.
        string numbered = Path.Combine(root, RefitModService.DirFor(mine + " (2)")!);
        Directory.CreateDirectory(numbered);
        RefitModService.WriteScaffold(numbered, mine + " (2)", "");

        Assert.Equal(numbered, RefitModService.Find(root, "Storm Private's Coat", "Neolithe"));
        Assert.Equal(theirs, RefitModService.Find(root, "Storm Privates Coat", "Neolithe"));
    }

    // ── writing into a mod Proteus made ────────────────────────────────────────────────────────────

    private const string Coat = "chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string Trousers = "chara/equipment/e6255/model/c0201e6255_dwn.mdl";

    [Fact]
    public void The_scaffold_is_a_manifest_the_retarget_writer_can_write_into()
    {
        string mod = Path.Combine(root, "coat");
        Directory.CreateDirectory(mod);
        RefitModService.WriteScaffold(mod, "Ala Mhigan Coat — Neolithe", "made by a test");

        // A folder without a manifest reads as pre-v4 and every write into it is refused.
        Assert.False(PenumbraModMeta.IsLegacyFolder(mod));
        Assert.Equal([PenumbraModMeta.MetaFile], Directory.GetFiles(mod).Select(Path.GetFileName));

        var outcome = BodyRetargetWriter.Save(mod, "Body — Ala Mhigan Coat", "Neolithe · SFW L", Coat, [1, 2, 3],
                                              "Neolithe", "The game's own body", "SFW L");
        Assert.True(outcome.Ok, outcome.Message);

        var options = PenumbraModMeta.TryReadFileOptions(mod, "Body — Ala Mhigan Coat")!;
        Assert.Equal([BodyRetargetWriter.OriginalOption, "Neolithe · SFW L"], options.Select(o => o.Name));
        Assert.Empty(options[0].Files);                                  // "Original" gives the game's coat back
        Assert.True(File.Exists(Path.Combine(mod, options[1].Files[Coat].Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Both_halves_of_one_outfit_land_in_one_option()
    {
        // A coat that covers chest and legs is two drawn models and two refits, but one garment: the second save
        // carries the first's game path along, so the player ticks one option and gets both.
        string mod = Path.Combine(root, "coat");
        Directory.CreateDirectory(mod);
        RefitModService.WriteScaffold(mod, "Ala Mhigan Coat — Neolithe", "");

        const string group = "Body — Ala Mhigan Coat";
        Assert.True(BodyRetargetWriter.Save(mod, group, "Neolithe · SFW L", Coat, [1], "Neolithe", "vanilla", "SFW L").Ok);
        Assert.True(BodyRetargetWriter.Save(mod, group, "Neolithe · SFW L", Trousers, [2], "Neolithe", "vanilla", "SFW L").Ok);

        var files = PenumbraModMeta.TryReadFileOptions(mod, group)!.Single(o => o.Name == "Neolithe · SFW L").Files;
        Assert.Equal([Trousers, Coat], files.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Undoing_the_last_refit_leaves_the_mod_standing_and_inert()
    {
        // Deleting a registered mod out from under Penumbra is not something this does; what it must not do is leave
        // a half-written group behind.
        string mod = Path.Combine(root, "coat");
        Directory.CreateDirectory(mod);
        RefitModService.WriteScaffold(mod, "Ala Mhigan Coat — Neolithe", "");

        const string group = "Body — Ala Mhigan Coat";
        Assert.True(BodyRetargetWriter.Save(mod, group, "Neolithe · SFW L", Coat, [1], "Neolithe", "vanilla", "SFW L").Ok);
        Assert.True(BodyRetargetWriter.Undo(mod, group, "Neolithe · SFW L").Ok);

        Assert.Null(PenumbraModMeta.TryReadFileOptions(mod, group));
        Assert.False(PenumbraModMeta.IsLegacyFolder(mod));
        Assert.Null(BodyRetargetWriter.ReadRecord(mod)?.Options.FirstOrDefault(o => o.Name == "Neolithe · SFW L"));
    }

    // ── reading a drawn model as an item ───────────────────────────────────────────────────────────

    [Fact]
    public void A_drawn_equipment_path_reads_as_an_item()
    {
        var pick = VanillaPick.From(Coat, (category, set) => category == 4 && set == 6255 ? "Ala Mhigan Coat" : null);

        Assert.NotNull(pick);
        Assert.Equal(Coat, pick!.Value.GamePath);
        Assert.Equal("Ala Mhigan Coat", pick.Value.ItemName);
        Assert.Equal("e6255", pick.Value.SetTag);
        Assert.Equal("Body — Ala Mhigan Coat", pick.Value.Label);
    }

    [Fact]
    public void An_item_the_game_s_sheet_cannot_name_falls_back_to_its_set()
    {
        var pick = VanillaPick.From(Coat, (_, _) => null);

        Assert.NotNull(pick);
        Assert.Equal("e6255", pick!.Value.ItemName);
        Assert.Equal("Body — e6255", pick.Value.Label);
    }

    [Fact]
    public void Anything_that_is_not_equipment_is_not_a_pick()
    {
        Assert.Null(VanillaPick.From("chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl", (_, _) => "Body"));
        Assert.Null(VanillaPick.From("chara/equipment/e6255/model/c0201e6255_met.mdl", (_, _) => "Hat"));
        Assert.Null(VanillaPick.From("", (_, _) => null));
    }
}
