using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Worn = Proteus.Services.AutoRefitDecisions.Worn;
using Refusal = Proteus.Services.AccessoryCover.Refusal;

namespace Proteus.Tests;

/// <summary>
/// The automatic refit of a garment worn on an accessory slot — a jacket on a necklace — against hand-built models and
/// mods: which slots are walked, which pieces are left alone, whether a piece covers the body, and what it is sized by
/// (<see cref="AccessoryCover"/>). Against the installed mods: <see cref="AccessoryRefitLocalTests"/>.
/// </summary>
public class AccessoryRefitTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201a0095_wrs_a.mtrl";
    private const string Jacket = "chara/accessory/a0095/model/c0201a0095_nek.mdl";
    private const string Top = "chara/equipment/e0041/model/c0201e0041_top.mdl";

    // ── what a walk sees ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-jacket-refit-9")]
    [Trait("Plan", "EDGE-jacket-refit-11")]
    [Trait("Plan", "EDGE-jacket-refit-12")]
    public void Every_accessory_slot_is_walked_and_the_emperors_set_is_kept()
    {
        var drawn = new HashSet<string>
        {
            "chara/accessory/a0053/model/c0201a0053_nek.mdl", "chara/accessory/a0095/model/c0201a0095_wrs.mdl",
            "chara/accessory/a0003/model/c0201a0003_ear.mdl", "chara/accessory/a0031/model/c0201a0031_ril.mdl",
            "chara/accessory/a0053/model/c0201a0053_rir.mdl", "chara/equipment/e0041/model/c0201e0041_top.mdl",
        };
        var garments = DrawnModelPaths.EquippedAccessoryGarmentModelsFromModels(drawn);
        Assert.Equal(["ear", "nek", "ril", "rir", "wrs"], garments.Keys.Order());
        Assert.Equal("chara/accessory/a0053/model/c0201a0053_nek.mdl", garments["nek"]);

        // The second skin's hosts are unchanged: no earrings.
        Assert.Equal(["nek", "ril", "rir", "wrs"], DrawnModelPaths.EquippedAccessoryModelsFromModels(drawn).Keys.Order());
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-9")]
    [Trait("Plan", "EDGE-jacket-refit-12")]
    public void The_games_and_proteus_own_accessories_are_never_refitted()
    {
        var game = new Worn(Jacket, null, null);
        Assert.Equal(AutoRefitDecisions.Skip.GameAccessory, AutoRefitDecisions.ShouldRefit("_nek", game, true, false));
        Assert.Equal(AutoRefitDecisions.Skip.GameAccessory, AutoRefitDecisions.ShouldRefit("_nek", game, false, false));
        Assert.Equal(AutoRefitDecisions.Skip.None, AutoRefitDecisions.ShouldRefit("_top", new Worn(Top, null, null), true, false));

        var carrier = new Worn("chara/accessory/a0053/model/c0201a0053_rir.mdl", SidecarDiscoveryService.ManagedModDir, "x.mdl");
        Assert.Equal(AutoRefitDecisions.Skip.Managed, AutoRefitDecisions.ShouldRefit("_rir", carrier, true, false));

        // A modder's jacket carried on the Emperor's New necklace is refitted like any.
        var modded = new Worn("chara/accessory/a0053/model/c0201a0053_nek.mdl", "Some Jacket", "jacket.mdl");
        Assert.Equal(AutoRefitDecisions.Skip.None, AutoRefitDecisions.ShouldRefit("_nek", modded, false, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-29")]
    [Trait("Plan", "EDGE-jacket-refit-27")]
    [Trait("Plan", "EDGE-outfit-fit-13")]   // a chest piece's job is done before the glove kept outside it
    public void Body_slots_are_worked_first_and_stay_body_only()
    {
        Assert.Equal(["_top", "_dwn", "_glv", "_sho"], AutoRefitDecisions.Slots);
        Assert.Equal(["_nek", "_ear", "_wrs", "_rir", "_ril"], AutoRefitDecisions.AccessorySlots);
        Assert.Equal(AutoRefitDecisions.Slots.Concat(AutoRefitDecisions.AccessorySlots), AutoRefitDecisions.WornSlots);
        Assert.All(AutoRefitDecisions.AccessorySlots, s => Assert.True(AutoRefitDecisions.IsAccessory(s)));
        Assert.All(AutoRefitDecisions.Slots, s => Assert.False(AutoRefitDecisions.IsAccessory(s)));

        var queued = new HashSet<string> { "_nek", "_top" };
        Assert.Equal("_top", AutoRefitDecisions.WornSlots.First(queued.Contains));

        // A newly worn accessory is noticed; the option drawn changing within its mod is not.
        var before = new Dictionary<string, Worn> { ["_nek"] = new(Jacket, "Mod", "a.mdl") };
        Assert.Empty(AutoRefitDecisions.Changed(before, new Dictionary<string, Worn> { ["_nek"] = new(Jacket, "Mod", "b.mdl") }));
        Assert.Equal(["_nek"], AutoRefitDecisions.Changed(before, new Dictionary<string, Worn> { ["_nek"] = new(Jacket, "Other", "a.mdl") }));
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-5")]
    public void Accessory_slots_are_named_in_the_players_language()
    {
        Assert.Equal(Proteus.Localization.Strings.Common.SlotNecklace, AutoRefitWatcher.SlotName("_nek"));
        Assert.Equal(Proteus.Localization.Strings.Common.SlotEarrings, AutoRefitWatcher.SlotName("_ear"));
        Assert.Equal(Proteus.Localization.Strings.Common.SlotBracelets, AutoRefitWatcher.SlotName("_wrs"));
        Assert.Equal(Proteus.Localization.Strings.Common.SlotRightRing, AutoRefitWatcher.SlotName("_rir"));
        Assert.Equal(Proteus.Localization.Strings.Common.SlotLeftRing, AutoRefitWatcher.SlotName("_ril"));
        Assert.Equal(Proteus.Localization.Strings.Parts.RetargetChest, AutoRefitWatcher.SlotName("_top"));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-22")]
    public void The_race_of_an_accessory_is_read_off_its_path()
    {
        Assert.Equal("0201", BodySizeCatalog.RaceOf("chara/accessory/a0095/model/c0201a0095_wrs.mdl"));
        Assert.Equal("0101", BodySizeCatalog.RaceOf("chara/accessory/a0003/model/c0101a0003_nek.mdl"));
        Assert.Equal("0201", BodySizeCatalog.RaceOf(Top));
        Assert.Null(BodySizeCatalog.RaceOf("chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl"));
        // Never a body part: the size set and the body index go on ignoring accessories.
        Assert.Null(BodySizeCatalog.SlotOf("chara/accessory/a0095/model/c0201a0095_wrs.mdl"));
        Assert.Equal((null, "0201"), AutoRefitWatcher.ReadAs(Jacket));
    }

    // ── a garment, or jewellery ──────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-jacket-refit-7")]
    [Trait("Plan", "EDGE-jacket-refit-14")]
    public void What_a_piece_covers_decides_what_it_is_refitted_as()
    {
        static bool None(string _) => false;
        static bool Top(string slot) => slot == "_top";

        var choker = new AccessoryCover.Cover(0.02f, 0f);
        Assert.True(choker.CoversNothing);
        Assert.Empty(choker.Covered(Top));

        // A big necklace covers more of the chest than a sleeve sized with its top does: alone, it is jewellery.
        Assert.Empty(new AccessoryCover.Cover(0.23f, 0f).Covered(None));
        Assert.Equal(["_top"], new AccessoryCover.Cover(0.10f, 0.02f).Covered(Top));
        Assert.Equal(["_top"], new AccessoryCover.Cover(0.6f, 0.05f).Covered(None));       // a cropped jacket alone

        var coat = new AccessoryCover.Cover(0.45f, 0.7f);                                  // a long coat
        Assert.Equal(["_dwn", "_top"], coat.Covered(None));
        Assert.Equal(["_top", "_dwn"], new AccessoryCover.Cover(0.5f, 0.5f).Covered(None)); // a tie goes to the chest
        Assert.Equal(["_top"], new AccessoryCover.Cover(0.3f, 0.3f).Covered(Top));         // legs not sized with it
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-23")]
    [Trait("Plan", "EDGE-jacket-refit-22")]
    public void A_jacket_over_a_chest_with_no_size_to_go_onto_says_why()
    {
        // Measured against the body's first chest when none is chosen, a jacket still covers it — not jewellery.
        BodyOption[] sizes = [ChestXs, ChestM];
        Assert.Equal(ChestXs, AccessoryCover.MeasuredOn(null, sizes));
        Assert.Equal(ChestM, AccessoryCover.MeasuredOn(ChestM, sizes));
        Assert.Null(AccessoryCover.MeasuredOn(null, []));

        var jacket = new AccessoryCover.Cover(0.6f, 0f).Covered(_ => false);
        var legsOnly = new BodyOption("Legs", "", "S", "s.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
        // No chest chosen: said so. A chest chosen that the body has none of at this race: the race's lack, as tops say.
        Assert.Equal(("_top", AccessoryCover.NoSize.Chosen),
                     AccessoryCover.WithoutSize(jacket, s => s == "_dwn" ? legsOnly : null, s => s == "_dwn"));
        Assert.Equal(("_top", AccessoryCover.NoSize.ForRace),
                     AccessoryCover.WithoutSize(jacket, s => s == "_dwn" ? legsOnly : null, _ => true));
        Assert.Null(AccessoryCover.WithoutSize(jacket, _ => ChestXs, _ => true));
        Assert.Null(AccessoryCover.WithoutSize([], _ => null, _ => true));   // jewellery says that instead
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-24")]
    [Trait("Plan", "AC-jacket-refit-5")]
    public void In_a_group_shared_with_its_top_an_accessory_joins_the_tops_refit_or_is_left_alone()
    {
        Assert.Equal("Neo — XS", AccessoryCover.SharedOption(shared: false, "Neo — XS", "Neo — XS", existing: false));
        Assert.Equal("Neo — XS + M", AccessoryCover.SharedOption(shared: true, "Neo — XS", "Neo — XS + M", existing: false));
        Assert.Equal("Neo — XS", AccessoryCover.SharedOption(shared: true, "Neo — XS", "Neo — XS", existing: true));
        Assert.Null(AccessoryCover.SharedOption(shared: true, "Neo — XS", "Neo — XS", existing: false));
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-7")]
    public void Jewellery_read_before_is_let_be_before_anything_is_gathered()
    {
        static bool None(string _) => false;
        var necklace = AccessoryCover.ToCache(new AccessoryCover.Cover(0.23f, 0f));   // Covered In Ice's, as measured
        Assert.True(AccessoryCover.KnownJewellery(necklace, None));
        Assert.True(AccessoryCover.KnownJewellery(AccessoryCover.ToCache(new AccessoryCover.Cover(0.01f, 0f)), _ => true));
        // Sized with a top, the same cover is a garment: gathered and refitted.
        Assert.False(AccessoryCover.KnownJewellery(necklace, slot => slot == "_top"));
        Assert.False(AccessoryCover.KnownJewellery(null, None));   // never read: measured
        Assert.False(AccessoryCover.KnownJewellery(new DetectionCache.Entry("x", "y", Confidence.Exact, false, []), None));
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-6")]
    public void A_source_made_on_the_games_own_body_is_read_in_the_games_own_sizes()
    {
        var game = new BodySizeCatalog("<game>", [ChestM]);
        var installed = new BodySizeCatalog("<neo>", [ChestXs]);
        Assert.Same(game, AccessoryCover.CatalogOf(VanillaBodyCatalog.Key, game, _ => installed));
        Assert.Same(installed, AccessoryCover.CatalogOf("neo", game, d => d == "neo" ? installed : null));
        Assert.Null(AccessoryCover.CatalogOf("gone", game, _ => null));
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-7")]
    [Trait("Plan", "EDGE-jacket-refit-13")]
    public void A_cover_reading_is_kept_by_content_body_and_sizes()
    {
        var cover = new AccessoryCover.Cover(0.61f, 0.03f);
        Assert.Equal(cover, AccessoryCover.FromCache(AccessoryCover.ToCache(cover)));
        Assert.Null(AccessoryCover.FromCache(new DetectionCache.Entry("x", "y", Confidence.Exact, false, [])));

        byte[] a = [1, 2, 3], b = [1, 2, 4];
        string key = AccessoryCover.Key(a, "Neo", "xs.mdl", "s.mdl", "0201");
        Assert.Equal(key, AccessoryCover.Key(a, "neo", "XS.mdl", "s.mdl", "0201"));
        Assert.NotEqual(key, AccessoryCover.Key(b, "Neo", "xs.mdl", "s.mdl", "0201"));
        Assert.NotEqual(key, AccessoryCover.Key(a, "Rue", "xs.mdl", "s.mdl", "0201"));
        Assert.NotEqual(key, AccessoryCover.Key(a, "Neo", "m.mdl", "s.mdl", "0201"));
        Assert.NotEqual(key, AccessoryCover.Key(a, "Neo", "xs.mdl", "m.mdl", "0201"));
        Assert.NotEqual(key, AccessoryCover.Key(a, "Neo", "xs.mdl", "s.mdl", "0101"));
    }

    /// <summary>A flat square <paramref name="half"/> m each way about (0, 1) at height <paramref name="z"/>, facing +Z.</summary>
    private static (float, float, float)[] Sheet(float half, float z, int n, float x0 = 0f)
    {
        var corners = new List<(float, float, float)>();
        float step = 2 * half / n;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float x = x0 - half + i * step, y = 1f - half + j * step;
                corners.AddRange([(x, y, z), (x + step, y, z), (x, y + step, z)]);
                corners.AddRange([(x + step, y, z), (x + step, y + step, z), (x, y + step, z)]);
            }
        return corners.ToArray();
    }

    private static ModelParts Read(string material, (float, float, float)[] corners)
        => ModelPartReader.Read(SyntheticModel.Build([], new SyntheticModel.Mesh(material, new SyntheticModel.Sub(0, Corners: corners))))!;

    [Fact]
    [Trait("Plan", "AC-jacket-refit-7")]
    [Trait("Plan", "AC-jacket-refit-5")]
    public void Cover_is_measured_from_the_body_out_to_the_cloth()
    {
        var chest = Read(Skin, Sheet(0.15f, 0f, 20));
        // A jacket over the whole chest; a small pendant over a corner of it; a ring well away from it.
        Assert.True(AccessoryCover.Share(Read(Cloth, Sheet(0.15f, 0.01f, 10)), chest) > 0.9f);
        float pendant = AccessoryCover.Share(Read(Cloth, Sheet(0.02f, 0.01f, 4, x0: 0.1f)), chest);
        Assert.InRange(pendant, 0.001f, AccessoryCover.Alone);
        Assert.Equal(0f, AccessoryCover.Share(Read(Cloth, Sheet(0.02f, 0.5f, 4)), chest));
        // Every vertex of the pendant is within reach of the chest — measured the other way it would read as covering it all.
        Assert.Empty(AccessoryCover.Read(Read(Cloth, Sheet(0.02f, 0.01f, 4, x0: 0.1f)), chest, null).Covered(_ => false));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-21")]
    public void Cloth_inside_the_body_it_would_be_refitted_from_is_not_its_source()
    {
        string dir = Path.Combine(Path.GetTempPath(), "proteus-accessory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string body = Path.Combine(dir, "chest.mdl");
            File.WriteAllBytes(body, SyntheticModel.Build([], new SyntheticModel.Mesh(Skin,
                new SyntheticModel.Sub(0, Corners: Sheet(0.12f, 0f, 24)))));
            Assert.True(AccessoryCover.ClearsSource(Read(Cloth, Sheet(0.1f, 0.005f, 14)), body));
            Assert.False(AccessoryCover.ClearsSource(Read(Cloth, Sheet(0.1f, -0.005f, 14)), body));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── the author's sizes ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-jacket-refit-8")]
    [Trait("Plan", "EDGE-jacket-refit-15")]
    public void An_option_names_a_body_by_a_word_no_bodys_sizes_use()
    {
        string[] bodies = ["Neolithe [ALL IN ONE]", "Neolithe [ALL IN ONE]", "Bibo+", "puffy pussy", "fix pubes",
                           "hs-Rue+-2.2.7-y0f", "hs-Yet Another Body+-4.2.0-tmi"];
        var sizes = new[] { "DEFAULT ALMOND · NSFW Almond XS", "Gen C Small", "Puffy", "Nude - Medium", "SFW Bra - Large" }
                    .SelectMany(AutoRefitDecisions.Words).ToHashSet();

        Assert.True(AutoRefitDecisions.NamesABody("Neolithe XS", bodies, sizes));
        Assert.True(AutoRefitDecisions.NamesABody("Bibo+ M", bodies, sizes));
        Assert.True(AutoRefitDecisions.NamesABody("Rue L", bodies, sizes));
        Assert.False(AutoRefitDecisions.NamesABody("Medium", bodies, sizes));
        Assert.False(AutoRefitDecisions.NamesABody("Puffy", bodies, sizes));          // a Neolithe size, not "puffy pussy"
        Assert.False(AutoRefitDecisions.NamesABody("Gen C", bodies, sizes));
        Assert.False(AutoRefitDecisions.NamesABody("Large (All in one)", bodies, sizes));
    }

    // ── what it is sized by ──────────────────────────────────────────────────────────────────────────────

    private static readonly BodyOption ChestXs = new("Chest", "", "XS", "xs.mdl", Top, "_top");
    private static readonly BodyOption ChestM = new("Chest", "", "M", "m.mdl", Top, "_top");

    private static AutoRefitDecisions.Pick Pick(BodyOption option, string dir = "bibo",
                                                Confidence confidence = Confidence.Likely)
        => new(dir, option, confidence, false);

    private static AccessoryCover.Beneath Beneath(bool gameOwn = false, BodyRetargetWriter.Entry? record = null,
                                                  PieceOutcome? outcome = null,
                                                  (AutoRefitDecisions.Pick?, bool)? read = null, string? chosen = "xs.mdl")
        => AccessoryCover.FromBeneath(gameOwn, record, "neo", "_top", chosen, outcome,
                                      () => read ?? throw new InvalidOperationException("read"),
                                      (dir, rel) => rel == "m.mdl" ? Pick(ChestM, dir, Confidence.Exact) : null);

    private static BodyRetargetWriter.Entry Refitted(string bodyMod = "neo", string? to = "xs.mdl", string? sourceRel = "m.mdl")
        => new()
        {
            BodyMod = bodyMod, SourceDir = sourceRel == null ? null : "bibo", SourceRel = sourceRel,
            Targets = to == null ? null : new Dictionary<string, string> { ["_top"] = to },
        };

    [Fact]
    [Trait("Plan", "AC-jacket-refit-6")]
    [Trait("Plan", "EDGE-jacket-refit-17")]
    public void The_games_own_top_beneath_says_nothing()
        => Assert.Equal(Refusal.GameBeneath, Beneath(gameOwn: true).Why);

    [Fact]
    [Trait("Plan", "AC-jacket-refit-6")]
    [Trait("Plan", "EDGE-jacket-refit-18")]
    public void A_refitted_top_beneath_says_what_it_was_made_from_when_it_is_on_the_chosen_size()
    {
        var ok = Beneath(record: Refitted());
        Assert.Null(ok.Why);
        Assert.Equal(ChestM, ok.Source!.Option);
        Assert.Equal("bibo", ok.Source.Dir);

        Assert.Equal(Refusal.NotOnTarget, Beneath(record: Refitted(bodyMod: "rue")).Why);
        Assert.Equal(Refusal.NotOnTarget, Beneath(record: Refitted(to: "s.mdl")).Why);       // onto another size
        Assert.Equal(Refusal.OldRecord, Beneath(record: Refitted(sourceRel: null)).Why);      // an earlier version's
        Assert.Equal(Refusal.OldRecord, Beneath(record: Refitted(to: null)).Why);
        Assert.Equal(Refusal.Unsure, Beneath(record: Refitted(sourceRel: "gone.mdl")).Why);   // its body changed since
        Assert.Equal(Refusal.NotOnTarget, Beneath(record: Refitted(), chosen: null).Why);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-6")]
    [Trait("Plan", "EDGE-jacket-refit-19")]
    public void A_top_refitted_in_this_batch_says_what_its_refit_came_to()
    {
        var made = Beneath(outcome: new PieceOutcome(true, Pick(ChestM)));
        Assert.Equal(ChestM, made.Source!.Option);
        Assert.Equal(Refusal.NotOnTarget, Beneath(outcome: new PieceOutcome(false, null)).Why);
        Assert.Equal(Refusal.Unsure, Beneath(outcome: new PieceOutcome(true, null)).Why);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-6")]
    [Trait("Plan", "EDGE-jacket-refit-20")]
    public void A_top_with_no_refit_this_batch_is_read_and_taken_only_on_the_chosen_size()
    {
        Assert.Equal(ChestXs, Beneath(read: (Pick(ChestXs, "neo"), true)).Source!.Option);
        Assert.Equal(Refusal.NotOnTarget, Beneath(read: (Pick(ChestM), false)).Why);
        Assert.Equal(Refusal.Unsure, Beneath(read: (Pick(ChestXs, "neo", Confidence.Ambiguous), true)).Why);
        Assert.Equal(Refusal.Unsure, Beneath(read: (null, false)).Why);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-6")]
    [Trait("Plan", "EDGE-jacket-refit-14")]
    public void Each_part_is_sized_by_its_sibling_or_what_is_worn_beneath_and_a_part_with_neither_is_left_out()
    {
        var legsS = new BodyOption("Legs", "", "S", "ls.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
        BodyOption? TargetFor(string slot) => slot == "_top" ? ChestXs : legsS;
        var beneathAsked = new List<string>();
        AccessoryCover.Beneath Under(string slot)
        {
            beneathAsked.Add(slot);
            return slot == "_top" ? new(Pick(ChestM), null) : new(null, Refusal.GameBeneath);
        }

        // A long coat: its chest from the top beside it in its options, its legs from nothing — left out.
        var coat = AccessoryCover.SourcesFor(["_top", "_dwn"], TargetFor, s => s == "_top" ? Pick(ChestM, "bibo") : null,
                                             Under, _ => true);
        Assert.Null(coat.Refused);
        Assert.Equal(["_top"], coat.Sources.Keys);
        Assert.Equal(["_dwn"], coat.LeftOut);
        Assert.Equal(["_dwn"], beneathAsked);   // a sibling read means the piece beneath is never asked

        // The piece's own part with nothing to go by: the piece is left as it is, and why is said.
        var bare = AccessoryCover.SourcesFor(["_dwn"], TargetFor, _ => null, Under, _ => true);
        Assert.Equal(Refusal.GameBeneath, bare.Refused);

        // Cloth sitting inside its supposed source: not its source.
        var inside = AccessoryCover.SourcesFor(["_top"], TargetFor, _ => Pick(ChestM), Under, _ => false);
        Assert.Equal(Refusal.OffBody, inside.Refused);

        // From beneath when there is no sibling.
        beneathAsked.Clear();
        var under = AccessoryCover.SourcesFor(["_top"], TargetFor, _ => null, Under, _ => true);
        Assert.Equal(ChestM, under.Sources["_top"].Option);
        Assert.Equal(["_top"], beneathAsked);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-8")]
    public void An_authors_size_for_a_body_is_left_to_them_unless_it_is_the_size_chosen()
    {
        Assert.Equal(AccessoryCover.AuthorGate.None, AccessoryCover.ForAuthor(null, Pick(ChestM), "neo", ChestXs));
        Assert.Equal(AccessoryCover.AuthorGate.AlreadyFits,
                     AccessoryCover.ForAuthor("Neolithe XS", Pick(ChestXs, "neo"), "neo", ChestXs));
        Assert.Equal(AccessoryCover.AuthorGate.LeftToAuthor,
                     AccessoryCover.ForAuthor("Neolithe M", Pick(ChestM, "neo"), "neo", ChestXs));
        Assert.Equal(AccessoryCover.AuthorGate.LeftToAuthor, AccessoryCover.ForAuthor("Neolithe M", null, "neo", ChestXs));
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-8")]
    [Trait("Plan", "EDGE-jacket-refit-16")]
    public void The_authors_option_is_the_one_selected_for_this_very_piece()
    {
        const string Ring = "chara/accessory/a0095/model/c0201a0095_rir.mdl";
        List<PenumbraModMeta.Redirect> redirects =
        [
            new(Jacket, "xs\\nek.mdl", "Top Size / Neolithe XS"), new(Top, "xs\\top.mdl", "Top Size / Neolithe XS"),
            new(Jacket, "common\\nek.mdl", "Top Size / Neolithe S"), new(Top, "s\\top.mdl", "Top Size / Neolithe S"),
            new(Jacket, "common\\nek.mdl", "Top Size / Neo — XS"), new(Top, "Body Retarget\\top.mdl", "Top Size / Neo — XS"),
            new(Ring, "ring\\a.mdl", "Ring Size / Neolithe Big"),
        ];
        var record = new BodyRetargetWriter.Record
        {
            Options = [new BodyRetargetWriter.Entry { Group = "Top Size", InAuthorGroup = true, Name = "Neo — XS", GamePath = Top }],
        };
        string[] bodies = ["Neolithe [ALL IN ONE]"];
        var sizeWords = new HashSet<string> { "almond", "gen" };
        string? Drawn(string selected, string gamePath = Jacket)
            => AccessoryCover.AuthorsBodyOption(gamePath, redirects, record, ["Top Size", "Ring Size"],
                                                g => g == "Top Size" ? [selected] : g == "Ring Size" ? ["Neolithe Big"] : null,
                                                bodies, sizeWords);

        Assert.Equal("Neolithe XS", Drawn("Neolithe XS"));     // what is selected now, not the first to serve the file
        Assert.Equal("Neolithe S", Drawn("Neolithe S"));
        Assert.Null(Drawn("Neo — XS"));                         // Proteus's own refit is no author's size
        // The ring's group names a body, but it sizes the ring, not this jacket.
        Assert.Null(AccessoryCover.AuthorsBodyOption(Jacket, redirects, record, ["Ring Size"],
                                                     _ => ["Neolithe Big"], bodies, sizeWords));
        Assert.Equal("Neolithe Big", Drawn("Neolithe XS", Ring));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-28")]
    public void An_outcome_is_known_by_the_piece_and_the_file_it_was_drawn_from()
    {
        var medium = new Worn(Top, "Scarlet", "top size/m/top.mdl");
        Assert.Equal(AutoRefitWatcher.OutcomeKey("_top", medium), AutoRefitWatcher.OutcomeKey("_top", medium with { Rel = "Top Size\\M\\top.mdl" }));
        Assert.NotEqual(AutoRefitWatcher.OutcomeKey("_top", medium), AutoRefitWatcher.OutcomeKey("_top", medium with { Rel = "top size/s/top.mdl" }));
        Assert.NotEqual(AutoRefitWatcher.OutcomeKey("_top", medium), AutoRefitWatcher.OutcomeKey("_dwn", medium));
    }

    // ── saved, joined, undone ────────────────────────────────────────────────────────────────────────────

    private static string NewMod()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-accessory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Jacket", "Someone", ""));
        return root;
    }

    private static readonly byte[] Model = SyntheticModel.Build([], new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0)));

    [Fact]
    [Trait("Plan", "AC-jacket-refit-5")]
    [Trait("Plan", "AC-jacket-refit-10")]
    [Trait("Plan", "EDGE-jacket-refit-25")]
    public void An_accessory_refit_is_saved_beside_its_top_and_undone_by_its_game_path()
    {
        string root = NewMod();
        try
        {
            var made = new BodyRetargetWriter.Provenance("bibo", "m.mdl", new Dictionary<string, string> { ["_top"] = "xs.mdl" });
            Assert.True(BodyRetargetWriter.Save(root, "Size", Top, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit("Neo — XS", Model, "XS")], auto: true,
                                                provenance: made).Ok);
            var record = BodyRetargetWriter.ReadRecord(root)!;
            // The jacket joins the top's automatic option rather than switching it off.
            string option = RefitCore.JoinOption(record, "Size", Jacket, "neo",
                                                 new Dictionary<string, string> { ["_top"] = "xs.mdl" },
                                                 new Dictionary<string, string> { ["_top"] = "xs.mdl" }, "Neo — XS (jacket)");
            Assert.Equal("Neo — XS", option);
            Assert.True(BodyRetargetWriter.Save(root, "Size", Jacket, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit(option, Model, "XS")], auto: true,
                                                provenance: made).Ok);

            var saved = PenumbraModMeta.TryReadFileOptions(root, "Size")!.Single(o => o.Name == "Neo — XS");
            Assert.StartsWith(BodyRetargetWriter.Subfolder + "/", saved.Files![Jacket]);
            Assert.EndsWith("chara/accessory/a0095/model/c0201a0095_nek.mdl", saved.Files[Jacket]);
            Assert.True(saved.Files.ContainsKey(Top));
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Neo — XS", Jacket, ("bibo", "m.mdl")));

            // Undone by the jacket's game path: the joined option goes, both pieces' automatic refits with it.
            var undone = AutoRefitUndo.Undo(root, Jacket, new HashSet<string>());
            Assert.NotNull(undone);
            Assert.Equal(["Neo — XS"], undone!.Removed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-10")]
    public void A_size_made_by_hand_for_an_accessory_is_not_undone()
    {
        string root = NewMod();
        try
        {
            Assert.True(BodyRetargetWriter.Save(root, "Size", Jacket, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit("Neo — XS", Model, "XS")], auto: false).Ok);
            Assert.Null(AutoRefitUndo.Undo(root, Jacket, new HashSet<string>()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
