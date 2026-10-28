using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Refitting and editing the game's own gear: reading the game's body as a catalog, naming the mod a refit goes into,
/// seeding the mod an edit goes into, and writing into a mod Proteus made rather than an author's. Over real temp folders, because what is being tested is
/// the shape of what lands on disk.
/// </summary>
[Collection(GameBodyCollection.Name)]
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
    public void The_game_s_chest_is_known_by_its_bytes()
    {
        // [AC-bra-hole-transfer-3] Only the game's own chest is read with its bra (BodyRetarget.SmallclothesOverHoles): known
        // by content, the extract's bytes, for the slot asked — wherever a body mod's file sits.
        // The game here ships a chest alone, so no other slot was extracted.
        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0201", Game(asked, VanillaBodyCatalog.GamePathOf("0201", "_top")));
        byte[] top = File.ReadAllBytes(catalog.PathOf(catalog.For("_top", "0201")[0]));

        Assert.True(VanillaBodyCatalog.IsGameModel(top, "_top"));
        Assert.False(VanillaBodyCatalog.IsGameModel(top, "_dwn"));   // the same bytes, asked for another slot
        var other = (byte[])top.Clone();
        other[^1] ^= 0xFF;
        Assert.False(VanillaBodyCatalog.IsGameModel(other, "_top"));   // a body bent or baked from it is not it
        Assert.False(VanillaBodyCatalog.IsGameModel(top[..^1], "_top"));

        VanillaBodyCatalog.CleanUp();
        Assert.False(VanillaBodyCatalog.IsGameModel(top, "_top"));   // a new session reads the game again
    }

    [Fact]
    public void A_race_drawn_on_another_s_chest_has_that_chest()
    {
        // A Miqo'te woman's chest is the Midlander woman's: the drawn extract is known as the game's chest too, and
        // extracting the same model again keeps one entry.
        var asked = new List<string>();
        var own = VanillaBodyCatalog.Read("0201", Game(asked, EverySlot("0201")));
        var drawn = VanillaBodyCatalog.Read("0801", Game(asked, EverySlot("0201")), drawn: true);
        byte[] top = File.ReadAllBytes(drawn.PathOf(drawn.For("_top", "0801")[0]));

        Assert.Equal(File.ReadAllBytes(own.PathOf(own.For("_top", "0201")[0])), top);
        Assert.True(VanillaBodyCatalog.IsGameModel(top, "_top"));
        Assert.Equal(1, VanillaBodyCatalog.GameModelCount("_top"));
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
    public void A_race_with_no_body_of_its_own_gets_the_body_the_game_draws_for_it()
    {
        // A Miqo'te woman's gear is drawn on the Midlander woman's body: the game ships no c0801 body. Filed under her
        // own race, so the picker's race rule finds it.
        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0801", Game(asked, EverySlot("0201")), drawn: true);

        Assert.Equal(["_top", "_dwn", "_glv", "_sho"], catalog.Slots);
        Assert.All(catalog.Options, o => Assert.Equal("0801", BodySizeCatalog.RaceOf(o.GamePath)));
        Assert.Single(catalog.For("_top", "0801"));
    }

    [Fact]
    public void Asked_for_its_own_body_a_race_that_ships_none_still_has_none()
    {
        // The racial route and the automatic refit read "no body" as the cue to bend a racial piece through a shared one.
        var asked = new List<string>();
        var read = Game(asked, EverySlot("0201"));

        Assert.False(VanillaBodyCatalog.Read("0801", read).IsBody);
        Assert.True(VanillaBodyCatalog.Read("0801", read, drawn: true).IsBody);   // the two are cached apart
        Assert.False(VanillaBodyCatalog.Read("0801", read).IsBody);
    }

    [Fact]
    public void A_fallback_across_sexes_still_reaches_the_picker()
    {
        // A Lalafell woman has her own chest and legs, but her hands and feet are the Lalafell man's.
        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("1201", Game(asked,
            VanillaBodyCatalog.GamePathOf("1201", "_top"), VanillaBodyCatalog.GamePathOf("1201", "_dwn"),
            VanillaBodyCatalog.GamePathOf("1101", "_glv"), VanillaBodyCatalog.GamePathOf("1101", "_sho")), drawn: true);

        Assert.Equal(["_top", "_dwn", "_glv", "_sho"], catalog.Slots);
        Assert.Single(catalog.For("_glv", "1201"));
        Assert.Contains(VanillaBodyCatalog.GamePathOf("1101", "_glv"), asked);
        Assert.DoesNotContain(VanillaBodyCatalog.GamePathOf("0201", "_glv"), asked);
    }

    [Theory]
    [InlineData("0801", "0801,0201,0101")]
    [InlineData("1501", "1501,0901,0101")]
    [InlineData("1201", "1201,1101,0101")]
    [InlineData("1404", "1404,1301,0101")]
    [InlineData("0301", "0301,0101")]
    [InlineData("0101", "0101")]
    public void The_fallback_chain_is_the_game_s(string race, string chain)
        => Assert.Equal(chain.Split(','), VanillaBodyCatalog.Chain(race));

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

    // ── one body for every reader, "no body" only for the reader that said so ──────────────────────

    /// <summary>The game's data as an object, so the same reader can be handed over as a method group.</summary>
    private sealed class CountingGame(params string[] has)
    {
        public readonly List<string> Asked = [];
        public byte[]? File(string path) => Game(Asked, has)(path);
    }

    [Fact]
    [Trait("Plan", "AC-game-body-reader-cache-1")]
    public void A_reader_that_finds_no_body_does_not_hide_it_from_one_that_does() // [AC-game-body-reader-cache-1]
    {
        // A test's stub reads nothing; the game's own reader, asking after it in the same process, still gets the body.
        Assert.False(VanillaBodyCatalog.Read("0201", _ => null).IsBody);

        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0201", Game(asked, EverySlot("0201")));

        Assert.True(catalog.IsBody);
        Assert.Equal(["_top", "_dwn", "_glv", "_sho"], catalog.Slots);
        Assert.All(catalog.Options, o => Assert.True(File.Exists(catalog.PathOf(o)), catalog.PathOf(o)));
        Assert.True(VanillaBodyCatalog.IsGameModel(File.ReadAllBytes(catalog.PathOf(catalog.For("_top", "0201")[0])), "_top"));
    }

    [Fact]
    [Trait("Plan", "AC-game-body-reader-cache-2")]
    [Trait("Plan", "EDGE-game-body-reader-cache-8")]
    public void A_body_read_once_is_every_reader_s_without_reading_again() // [AC-game-body-reader-cache-2] [EDGE-game-body-reader-cache-8]
    {
        // The plugin hands the catalog three readers of one game; the body is still extracted once.
        List<string> askedA = [], askedB = [];
        var readerA = Game(askedA, EverySlot("0201"));
        var readerB = Game(askedB, EverySlot("0201"));
        Assert.False(ReferenceEquals(readerA, readerB));

        var a = VanillaBodyCatalog.Read("0201", readerA);
        var b = VanillaBodyCatalog.Read("0201", readerB);

        Assert.Equal(4, askedA.Count);
        Assert.Empty(askedB);
        Assert.Equal(a.Options.Select(a.PathOf), b.Options.Select(b.PathOf));
    }

    [Fact]
    [Trait("Plan", "AC-game-body-reader-cache-3")]
    [Trait("Plan", "EDGE-game-body-reader-cache-4")]
    public void A_race_with_no_body_is_asked_of_the_game_once_a_reader() // [AC-game-body-reader-cache-3] [EDGE-game-body-reader-cache-4]
    {
        // The Studio asks every frame whether a racial piece's race has a body of its own; the answer is read once.
        var asked = new List<string>();
        var read = Game(asked, EverySlot("0201"));

        for (int i = 0; i < 3; i++) Assert.False(VanillaBodyCatalog.Read("0801", read).IsBody);
        Assert.Equal(4, asked.Count);

        // Its own "no body" says nothing of the body the game draws for it.
        Assert.True(VanillaBodyCatalog.Read("0801", read, drawn: true).IsBody);
        Assert.True(asked.Count > 4);
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-1")]
    public void A_stub_that_draws_no_body_does_not_hide_the_drawn_body() // [EDGE-game-body-reader-cache-1]
    {
        Assert.False(VanillaBodyCatalog.Read("0801", _ => null, drawn: true).IsBody);

        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0801", Game(asked, EverySlot("0201")), drawn: true);

        Assert.Equal(["_top", "_dwn", "_glv", "_sho"], catalog.Slots);
        Assert.All(catalog.Options, o => Assert.Equal("0801", BodySizeCatalog.RaceOf(o.GamePath)));
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-2")]
    public void A_reader_after_the_body_is_extracted_rewrites_nothing() // [EDGE-game-body-reader-cache-2]
    {
        var asked = new List<string>();
        var body = VanillaBodyCatalog.Read("0201", Game(asked, EverySlot("0201")));
        var before = body.Options.Select(body.PathOf)
                         .ToDictionary(p => p, p => (Bytes: File.ReadAllBytes(p), Written: File.GetLastWriteTimeUtc(p)));

        var stubAsked = new List<string>();
        var stub = VanillaBodyCatalog.Read("0201", p => { stubAsked.Add(p); return null; });

        Assert.True(stub.IsBody);
        Assert.Empty(stubAsked);
        foreach (var (path, was) in before)
        {
            Assert.Equal(was.Bytes, File.ReadAllBytes(path));
            Assert.Equal(was.Written, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-3")]
    public void A_reader_that_throws_does_not_hide_the_body() // [EDGE-game-body-reader-cache-3]
    {
        int thrown = 0;
        Func<string, byte[]?> broken = _ => { thrown++; throw new IOException("no game here"); };
        Assert.False(VanillaBodyCatalog.Read("0201", broken).IsBody);
        Assert.False(VanillaBodyCatalog.Read("0201", broken).IsBody);
        Assert.Equal(4, thrown);   // its "no body" is recorded for it, like any reader's

        var asked = new List<string>();
        var catalog = VanillaBodyCatalog.Read("0201", Game(asked, EverySlot("0201")));

        Assert.True(catalog.IsBody);
        Assert.Equal(4, catalog.Slots.Count());
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-5")]
    public void A_new_session_asks_a_reader_that_found_nothing_again() // [EDGE-game-body-reader-cache-5]
    {
        var asked = new List<string>();
        var read = Game(asked, EverySlot("0201"));

        VanillaBodyCatalog.Read("0801", read);
        Assert.Equal(4, asked.Count);

        VanillaBodyCatalog.CleanUp();
        VanillaBodyCatalog.Read("0801", read);
        Assert.Equal(8, asked.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-6")]
    public void Readers_racing_for_one_race_extract_it_once() // [EDGE-game-body-reader-cache-6]
    {
        int reads = 0;
        var bodies = new BodySizeCatalog[8];
        Parallel.For(0, bodies.Length, i =>
        {
            var serve = Game([], EverySlot("0201"));
            bodies[i] = VanillaBodyCatalog.Read("0201", p => { Interlocked.Increment(ref reads); return serve(p); });
        });

        Assert.Equal(4, reads);
        Assert.All(bodies, b => Assert.True(b.IsBody));
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-7")]
    public void A_partial_body_is_a_body_for_every_reader() // [EDGE-game-body-reader-cache-7]
    {
        List<string> askedA = [], askedB = [];
        var a = VanillaBodyCatalog.Read("0201", Game(askedA, VanillaBodyCatalog.GamePathOf("0201", "_top"),
                                                             VanillaBodyCatalog.GamePathOf("0201", "_dwn")));
        var b = VanillaBodyCatalog.Read("0201", Game(askedB, EverySlot("0201")));

        Assert.Equal(["_top", "_dwn"], a.Slots);
        Assert.Equal(["_top", "_dwn"], b.Slots);
        Assert.Empty(askedB);
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-9")]
    public void The_same_reader_in_another_delegate_is_not_asked_again() // [EDGE-game-body-reader-cache-9]
    {
        // Each method-group conversion is a new delegate over the same object and method: the same reader.
        var game = new CountingGame(EverySlot("0201"));
        VanillaBodyCatalog.Read("0801", game.File);
        VanillaBodyCatalog.Read("0801", game.File);
        Assert.Equal(4, game.Asked.Count);

        // A lambda evaluated twice over one closure is the same reader too. It captures only `game`, declared at method
        // scope, so both evaluations share one closure.
        VanillaBodyCatalog.CleanUp();
        for (int i = 0; i < 2; i++) VanillaBodyCatalog.Read("0801", p => game.File(p));
        Assert.Equal(8, game.Asked.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-10")]
    public void A_reader_built_afresh_each_call_is_a_new_reader() // [EDGE-game-body-reader-cache-10]
    {
        // What Read's doc warns against: a wrapper whose closure is new on every call reads the game every call.
        var game = new CountingGame(EverySlot("0201"));
        BodySizeCatalog Ask()
        {
            var g = game;   // the lambda captures this local, so each call makes a new closure
            return VanillaBodyCatalog.Read("0801", p => g.File(p));
        }

        Ask();
        Ask();
        Assert.Equal(8, game.Asked.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-game-body-reader-cache-11")]
    public void A_reader_that_found_nothing_is_not_kept_alive_by_the_catalog() // [EDGE-game-body-reader-cache-11]
    {
        // A real-data test's reader holds a whole Lumina.GameData; remembering that it found nothing must not pin it.
        var weak = AskWithAReaderNothingElseHolds();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive);

        // The record it left behind is forgotten as the race is next asked, and does not answer for anyone else.
        var game = new CountingGame(EverySlot("0201"));
        Assert.False(VanillaBodyCatalog.Read("0801", game.File).IsBody);
        Assert.Equal(4, game.Asked.Count);
    }

    private static int staticAsked;

    private static byte[]? NoGame(string path)
    {
        staticAsked++;
        return null;
    }

    [Fact]
    [Trait("Plan", "AC-game-body-reader-cache-3")]
    public void A_static_reader_that_found_nothing_is_remembered_for_the_session() // [AC-game-body-reader-cache-3]
    {
        // A static method has no target for the record to lose: it is the same reader every time, whatever is collected.
        staticAsked = 0;
        Func<string, byte[]?> first = NoGame;
        Assert.Null(first.Target);

        VanillaBodyCatalog.Read("0801", NoGame);
        VanillaBodyCatalog.Read("0801", NoGame);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        VanillaBodyCatalog.Read("0801", NoGame);

        Assert.Equal(4, staticAsked);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AskWithAReaderNothingElseHolds()
    {
        // A fresh heap object, held only by the reader's closure: nothing but the catalog could keep it alive.
        var sentinel = new object();
        int asked = 0;
        Func<string, byte[]?> read = _ => { asked++; return sentinel.GetHashCode() < 0 ? [] : null; };

        Assert.False(VanillaBodyCatalog.Read("0801", read).IsBody);
        Assert.False(VanillaBodyCatalog.Read("0801", read).IsBody);
        Assert.Equal(4, asked);   // the second answer came from the record, so there was one to let go of
        return new WeakReference(sentinel);
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
    public void Anything_that_is_not_gear_is_not_a_pick()
    {
        Assert.Null(VanillaPick.From("chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl", (_, _) => "Body"));
        Assert.Null(VanillaPick.From("", (_, _) => null));
    }

    [Fact]
    public void A_hat_or_a_ring_is_a_pick_but_not_a_refit()
    {
        // Every tool but Body size can edit them; Body size has no body under them to refit onto.
        var hat = VanillaPick.From("chara/equipment/e6255/model/c0201e6255_met.mdl", (_, _) => "Hat");
        var ring = VanillaPick.From("chara/accessory/a0053/model/c0201a0053_rir.mdl", (_, _) => "Ring");

        Assert.NotNull(hat);
        Assert.NotNull(ring);
        Assert.False(hat!.Value.Refittable);
        Assert.False(ring!.Value.Refittable);
        Assert.True(VanillaPick.From(Coat, (_, _) => "Coat")!.Value.Refittable);
    }

    // ── editing the game's own gear in a mod of its own ───────────────────────────────────────────

    [Fact]
    public void A_seeded_mod_publishes_the_game_s_model_at_its_own_path()
    {
        string mod = Path.Combine(root, "coat");
        Directory.CreateDirectory(mod);
        RefitModService.WriteScaffold(mod, "Ala Mhigan Coat — " + RefitModService.EditQualifier, "");

        Assert.True(RefitModService.AddGameModel(mod, Coat, [1, 2, 3]));

        var published = PenumbraModMeta.ReadAllRedirects(mod).Single();
        Assert.Equal(Coat, published.GamePath);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(mod, published.File.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(PenumbraModMeta.IsLegacyFolder(mod));
    }

    [Fact]
    public void Seeding_again_keeps_the_edited_model_and_the_mod_s_other_redirects()
    {
        string mod = Path.Combine(root, "coat");
        Directory.CreateDirectory(mod);
        RefitModService.WriteScaffold(mod, "Ala Mhigan Coat — " + RefitModService.EditQualifier, "");
        Assert.True(RefitModService.AddGameModel(mod, Coat, [1, 2, 3]));

        // An edit made since, and the other half of the outfit opened afterwards.
        string coatFile = Path.Combine(mod, Coat.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(coatFile, [9, 9]);
        Assert.True(RefitModService.AddGameModel(mod, Trousers, [4, 5]));

        Assert.False(RefitModService.AddGameModel(mod, Coat, [1, 2, 3]));   // nothing to do
        Assert.Equal([9, 9], File.ReadAllBytes(coatFile));
        Assert.Equal([Trousers, Coat],
                     PenumbraModMeta.ReadAllRedirects(mod).Select(r => r.GamePath).OrderBy(p => p, StringComparer.Ordinal));
    }
}
