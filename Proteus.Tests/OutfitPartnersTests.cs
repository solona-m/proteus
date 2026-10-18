using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Tests;

/// <summary>
/// Which piece a glove, shoe or accessory is kept outside (see <see cref="OutfitPartners"/>) — only one already on the
/// size chosen, read as made on the same size — and what its refit records of it.
/// </summary>
public class OutfitPartnersTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Top = "chara/equipment/e6010/model/c0201e6010_top.mdl";
    private const string Glove = "chara/equipment/e0068/model/c0201e0068_glv.mdl";
    private const string AuthorsTop = "chest size/small/top.mdl";
    private const string Chest = "nude/medium.mdl";
    private const string Onto = "rue/large.mdl";

    private static readonly SiblingPiece.Candidate Sibling = new(Top, AuthorsTop, "Chest Size / Small");

    private static AutoRefitDecisions.Pick Pick(string dir = "Bibo+", string rel = Chest)
        => new(dir, new BodyOption("Chest", "", "M", rel, Top, "_top"), Confidence.Exact, false);

    private static readonly byte[] Model = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0)));

    private static BodyRetargetWriter.Provenance Made(string dir = "Bibo+", string rel = Chest, string to = Onto)
        => new(dir, rel, new Dictionary<string, string> { ["_top"] = to });

    private static string NewMod()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-outfit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Geometric", "Someone", ""));
        Directory.CreateDirectory(Path.Combine(root, "chest size", "small"));
        File.WriteAllBytes(Path.Combine(root, "chest size", "small", "top.mdl"), Model);
        return root;
    }

    private static void Save(string root, string option, string gamePath, BodyRetargetWriter.Provenance? made,
                             byte[]? model = null, IReadOnlyDictionary<string, string>? layers = null)
        => Assert.True(BodyRetargetWriter.Save(root, "Size", gamePath, "rue", "Bibo+ — M",
                                               [new BodyRetargetWriter.Refit(option, model ?? Model, "L", layers)],
                                               auto: true, provenance: made).Ok);

    private static string FileOf(string root, string option, string gamePath)
        => BodyRetargetWriter.ReadRecord(root)!.Options.Single(e => e.Name == option && e.GamePath == gamePath).File;

    [Fact]
    [Trait("Plan", "AC-outfit-fit-4")]
    [Trait("Plan", "EDGE-outfit-fit-9")]
    public void The_size_set_takes_the_siblings_refit_in_the_same_option()
    {
        string root = NewMod();
        try
        {
            Save(root, "Rue L", Top, Made());
            var record = BodyRetargetWriter.ReadRecord(root);
            var notes = new List<string>();

            var partner = OutfitPartners.ForSizeSet(record, "Rue L", Sibling, "_top", Pick(), Onto, notes.Add);
            Assert.NotNull(partner);
            Assert.Equal(AuthorsTop, partner!.AuthoredRel);
            Assert.Equal(FileOf(root, "Rue L", Top), partner.RefittedRel);

            // No refit of the top in that option: no partner, said.
            Assert.Null(OutfitPartners.ForSizeSet(record, "Rue M", Sibling, "_top", Pick(), Onto, notes.Add));
            // A refit for another size, or made from another reading: no partner.
            Assert.Null(OutfitPartners.ForSizeSet(record, "Rue L", Sibling, "_top", Pick(), "rue/small.mdl", notes.Add));
            Assert.Null(OutfitPartners.ForSizeSet(record, "Rue L", Sibling, "_top", Pick("[IVCS] Bibo+"), Onto, notes.Add));
            Assert.Equal(3, notes.Count);

            // The size set records no size on its entries: the option is the size.
            Save(root, "Rue M", Top, null);
            var sized = OutfitPartners.ForSizeSet(BodyRetargetWriter.ReadRecord(root), "Rue M", Sibling, "_top", Pick(),
                                                  Onto, notes.Add);
            Assert.Equal(FileOf(root, "Rue M", Top), sized!.RefittedRel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-outfit-fit-3")]
    [Trait("Plan", "EDGE-outfit-fit-13")]
    public void The_automatic_refit_takes_this_batchs_refit_of_the_sibling_worn_with_it()
    {
        string root = NewMod();
        var notes = new List<string>();
        try
        {
            // The bra worn at the walk is the author's; its own job refitted it onto the size chosen.
            var refitted = new PieceOutcome(true, Pick(), "body retarget/top/rue l.mdl");
            var partner = OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, AuthorsTop,
                                                      refitted, null, notes.Add);
            Assert.Equal("body retarget/top/rue l.mdl", partner!.RefittedRel);

            // Its refit failed, was declined, or it was read as made on another size: none.
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, AuthorsTop,
                                                    new PieceOutcome(false, null), null, notes.Add));
            // On the size chosen, but by a file not known — the author's other size switched on: none.
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, AuthorsTop,
                                                    new PieceOutcome(true, Pick()), null, notes.Add));
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, AuthorsTop,
                                                    new PieceOutcome(true, Pick("[IVCS] Bibo+"), "x.mdl"), null, notes.Add));
            // Not refitted in this batch and still the author's file: on the old body, so none — never read on the new.
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, AuthorsTop, null, null,
                                                    notes.Add));
            // Another top worn with it — another option's, another mod's, or none at all.
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, "chest size/large/top.mdl",
                                                    refitted, null, notes.Add));
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, Path.GetTempPath(), Top, AuthorsTop,
                                                    refitted, null, notes.Add));
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, null, null, null, null, null,
                                                    notes.Add));
            Assert.Equal(7, notes.Count);

            // A refitted file that cannot be read: no outfit, said — the refit goes ahead as before.
            int said = notes.Count;
            Assert.Null(OutfitPartners.Load(root, new OutfitPartners.Partner("_top", AuthorsTop, "body retarget/gone.mdl"),
                                            notes.Add));
            Assert.True(notes.Count > said);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-15")]
    public void A_partner_on_another_body_with_the_same_file_is_refused()
    {
        string root = NewMod();
        var notes = new List<string>();
        try
        {
            Save(root, "Rue L", Top, Made());   // recorded onto "rue"
            string rel = FileOf(root, "Rue L", Top);
            var record = BodyRetargetWriter.ReadRecord(root);
            Assert.NotNull(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, rel, null, record,
                                                       notes.Add, targetDir: "rue"));
            // A fork naming its file as Rue does: the same file on another body is another size.
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, rel, null, record,
                                                    notes.Add, targetDir: "LavaBod+ LaRue"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-outfit-fit-3")]
    [Trait("Plan", "EDGE-outfit-fit-10")]
    [Trait("Plan", "EDGE-outfit-fit-11")]
    public void A_refit_worn_from_before_is_a_partner_only_when_its_record_says_it_is_on_the_size_chosen()
    {
        string root = NewMod();
        var notes = new List<string>();
        try
        {
            Save(root, "Rue L", Top, Made());
            string rel = FileOf(root, "Rue L", Top);
            var record = BodyRetargetWriter.ReadRecord(root);

            var partner = OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, rel, null, record,
                                                      notes.Add);
            Assert.Equal(rel, partner!.RefittedRel);
            Assert.Equal(AuthorsTop, partner.AuthoredRel);

            // For another size, or made from another reading.
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), "rue/small.mdl", root, Top, rel, null,
                                                    record, notes.Add));
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick("[IVCS] Bibo+"), Onto, root, Top, rel,
                                                    null, record, notes.Add));
            // A refit from before sizes were recorded says nothing.
            Save(root, "Rue M", Top, null);
            string old = FileOf(root, "Rue M", Top);
            Assert.Null(OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, old, null,
                                                    BodyRetargetWriter.ReadRecord(root), notes.Add));
            Assert.Equal(3, notes.Count);

            // An author's file that already fits is its own refitted file.
            var fits = OutfitPartners.ForAutoRefit(root, Sibling, "_top", Pick(), Onto, root, Top, AuthorsTop,
                                                   new PieceOutcome(true, Pick(), AuthorsTop), null, notes.Add);
            var outfit = OutfitPartners.Load(root, fits!, notes.Add);
            Assert.Same(outfit!.Authored, outfit.Refitted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-outfit-fit-4")]
    [Trait("Plan", "EDGE-outfit-fit-12")]
    public void A_refit_kept_outside_another_piece_is_made_again_rather_than_put_back()
    {
        string root = NewMod();
        try
        {
            Save(root, "Rue L", Top, Made());
            var partner = new OutfitPartners.Partner("_top", AuthorsTop, FileOf(root, "Rue L", Top));
            var layers = OutfitPartners.LayersOf(root, partner);
            Save(root, "Rue L", Glove, Made(), layers: layers);

            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue L", Glove, null, layers));
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue L", Glove, null, layers, forced: true));
            // The top remade in place, under the same name: its content differs, so the glove is made again.
            Save(root, "Rue L", Top, Made(),
                 SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, OffsetX: 3f))));
            var now = OutfitPartners.LayersOf(root, partner);
            Assert.NotEqual(layers["_top"], now["_top"]);
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Rue L", Glove, null, now));
            // Worn without its top now: as good as any, not made again on every switch.
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue L", Glove));

            // A glove saved before outfits were kept: put back, unless the player asked for what they wear now.
            Save(root, "Rue M", Glove, Made());
            Assert.Null(BodyRetargetWriter.ReadRecord(root)!.Options.Single(e => e.Name == "Rue M" && e.GamePath == Glove)
                                                            .Layers);
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue M", Glove, null, now));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Rue M", Glove, null, now, forced: true));
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Rue M", Glove, null, null, forced: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-outfit-fit-4")]
    public void Layers_are_recorded_and_old_entries_read_none()
    {
        string root = NewMod();
        try
        {
            var layers = new Dictionary<string, string> { ["_top"] = "body retarget/top/rue l.mdl|0123abcd" };
            Save(root, "Rue L", Glove, Made(), layers: layers);
            Save(root, "Rue L", Top, Made());

            var entries = BodyRetargetWriter.ReadRecord(root)!.Options;
            Assert.Equal(layers, entries.Single(e => e.GamePath == Glove).Layers);
            Assert.Null(entries.Single(e => e.GamePath == Top).Layers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
