using System;
using System.Collections.Generic;
using System.Linq;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// An imported pack's <c>Imc</c> groups judge only their own slot's pieces, read off the piece's recorded slot when its
/// model sits in the archive off a game path. Neolithe Piercings (Empyrean Ring) is the shape: with the slot unread,
/// the left-ring group — which has no options for bits a–d or h — hid the chest's collarbone, chest and nipple
/// piercings whatever was ticked.
/// </summary>
public class ContentPieceSlotTests
{
    private const string Chest = "chest/neolithe nsfw hazelnut s.mdl";
    private const string Legs = "legs/neolithe gen b small.mdl";
    private const string ChestGroup = "Chest piercings";
    private const string LegsGroup = "Legs piercings";

    private static readonly string[] ChestAttrs = Parts(8);
    private static readonly string[] LegsAttrs = Parts(7);

    private static string[] Parts(int n) => [.. Enumerable.Range(0, n).Select(i => $"atr_rv_{(char)('a' + i)}")];

    private static string[] Letters(params char[] letters) => [.. letters.Select(c => $"atr_rv_{c}")];

    private static ContentAttributeGroup Group(string name, string? slot, int setId,
                                               params (string Name, int Mask)[] options) => new()
    {
        Group = name,
        SetId = setId,
        Slot = slot,
        DefaultMask = 0,
        Options = options.ToDictionary(o => o.Name, o => o.Mask, StringComparer.Ordinal),
    };

    /// <summary>The two groups exactly as Nao's imported sidecar records them.</summary>
    private static List<ContentAttributeGroup> NaosGroups() =>
    [
        Group(ChestGroup, "RFinger", 103, ("Collar bones dermals", 1), ("Collar bones dermals bottom", 128),
              ("Chest dermals", 2), ("Belly dermals", 16), ("Belly button piercing", 32),
              ("Belly button piercing heart charm", 64), ("NSFW ONLY: Nipples piercings", 4),
              ("NSFW ONLY: Nipples hearts piercings", 8)),
        Group(LegsGroup, "LFinger", 103, ("Hips dermals", 16), ("Christina (NSFW ONLY)", 32),
              ("Vertical hood (GEN B & C ONLY)", 64)),
    ];

    private static Dictionary<string, List<string>> AllOf(IEnumerable<ContentAttributeGroup> groups)
        => groups.ToDictionary(g => g.Group, g => g.Options.Keys.ToList(), StringComparer.Ordinal);

    private static Dictionary<string, List<string>> Sel(params (string Group, string[] On)[] picks)
        => picks.ToDictionary(p => p.Group, p => p.On.ToList(), StringComparer.Ordinal);

    private static string[] Hidden(IReadOnlyList<ContentAttributeGroup> groups, string model, string? slot,
                                   IReadOnlyList<string> attrs, IReadOnlyDictionary<string, List<string>>? sel)
        => [.. (ContentPieceResolver.HiddenAttributes(groups, model, slot, attrs, sel) ?? new HashSet<string>()).Order()];

    private static ContentVisibility For(IReadOnlyList<ContentAttributeGroup> groups, string model, string? slot,
                                         IReadOnlyList<string> attrs, IReadOnlyDictionary<string, List<string>>? sel)
        => ContentImcVisibility.For(groups, null, model, slot, attrs, sel, (_, _) => null);

    /// <summary>[AC-imc-group-piece-slot-1] [EDGE-imc-group-piece-slot-9] The right-ring piece follows the right-ring group alone.</summary>
    [Fact]
    public void A_right_ring_piece_stored_off_a_game_path_is_judged_only_by_the_right_ring_group()
    {
        var groups = NaosGroups();

        // Nao's state: everything ticked. Before, the legs mask hid a, b, c, d and h here.
        Assert.Empty(Hidden(groups, Chest, "Right ring", ChestAttrs, AllOf(groups)));

        // Only the chest dermals: everything but part B goes, and the legs group's ticks change nothing.
        var chestOnly = Sel((ChestGroup, ["Chest dermals"]));
        Assert.Equal(Letters('a', 'c', 'd', 'e', 'f', 'g', 'h'), Hidden(groups, Chest, "Right ring", ChestAttrs, chestOnly));
        foreach (var legs in new[] { Array.Empty<string>(), ["Hips dermals"], [.. groups[1].Options.Keys] })
            Assert.Equal(Letters('a', 'c', 'd', 'e', 'f', 'g', 'h'), Hidden(groups, Chest, "Right ring", ChestAttrs,
                Sel((ChestGroup, ["Chest dermals"]), (LegsGroup, legs))));
    }

    /// <summary>[AC-imc-group-piece-slot-2] [EDGE-imc-group-piece-slot-10] The left-ring piece follows the left-ring group alone.</summary>
    [Fact]
    public void A_left_ring_piece_follows_only_the_left_ring_group()
    {
        var groups = NaosGroups();
        var hips = Sel((LegsGroup, ["Hips dermals"]));
        Assert.Equal(Letters('a', 'b', 'c', 'd', 'f', 'g'), Hidden(groups, Legs, "Left ring", LegsAttrs, hips));

        // Every chest option ticked as well: the legs piece does not move.
        Assert.Equal(Letters('a', 'b', 'c', 'd', 'f', 'g'), Hidden(groups, Legs, "Left ring", LegsAttrs,
            Sel((LegsGroup, ["Hips dermals"]), (ChestGroup, [.. groups[0].Options.Keys]))));

        // Bits a–d have no left-ring option, so they stay off, as on the pack's own left ring; the tags still go.
        Assert.True(ContentPieceResolver.GovernsModel(groups, Legs, "Left ring"));
        Assert.True(For(groups, Legs, "Left ring", LegsAttrs, hips).OwnAttributes);
    }

    /// <summary>[AC-imc-group-piece-slot-4] [EDGE-imc-group-piece-slot-2] Without a recorded slot every group judges it, as before.</summary>
    [Fact]
    public void A_piece_with_no_recorded_slot_is_governed_by_every_group_as_before()
    {
        var groups = NaosGroups();
        Assert.Equal(Letters('a', 'b', 'c', 'd', 'h'), Hidden(groups, Chest, null, ChestAttrs, AllOf(groups)));
        Assert.Equal(Letters('a', 'b', 'c', 'd'), Hidden(groups, Legs, null, LegsAttrs, AllOf(groups)));
    }

    /// <summary>[EDGE-imc-group-piece-slot-3] A recorded slot that is not an equipment slot counts as none.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("Hair")]
    [InlineData("Body (dress)")]
    [InlineData("")]
    public void An_unknown_piece_slot_label_is_treated_as_no_slot(string slot)
    {
        var groups = NaosGroups();
        Assert.Equal(Hidden(groups, Chest, null, ChestAttrs, AllOf(groups)),
                     Hidden(groups, Chest, slot, ChestAttrs, AllOf(groups)));
    }

    /// <summary>[EDGE-imc-group-piece-slot-3] Only the ten equipment labels are trusted, in their own casing.</summary>
    [Theory]
    [InlineData("Head", "Head")]
    [InlineData("body", "Body")]
    [InlineData("Hands", "Hands")]
    [InlineData("Legs", "Legs")]
    [InlineData("Feet", "Feet")]
    [InlineData("Earrings", "Earrings")]
    [InlineData("Necklace", "Necklace")]
    [InlineData("Bracelets", "Bracelets")]
    [InlineData("RIGHT RING", "Right ring")]
    [InlineData("Left ring", "Left ring")]
    [InlineData("Hair", null)]
    [InlineData("Face", null)]
    [InlineData("rir", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Equip_label_accepts_only_the_equipment_slots(string? label, string? expected)
        => Assert.Equal(expected, ContentSlot.EquipLabel(label));

    /// <summary>[EDGE-imc-group-piece-slot-4] A group naming no slot, or one nobody knows, still judges every piece.</summary>
    [Fact]
    public void A_group_naming_no_slot_still_governs_a_piece_with_a_known_slot()
    {
        foreach (var slot in new string?[] { null, "Glasses" })
        {
            List<ContentAttributeGroup> groups = [Group("Toggles", slot, 103, ("Show A", 1))];
            Assert.True(ContentPieceResolver.GovernsModel(groups, Chest, "Right ring"));
            Assert.Equal(Letters('b', 'c'), Hidden(groups, Chest, "Right ring", Parts(3), Sel(("Toggles", ["Show A"]))));
        }
    }

    /// <summary>[EDGE-imc-group-piece-slot-5] The recorded slot matches whatever its casing.</summary>
    [Fact]
    public void A_piece_slot_matches_case_insensitively()
    {
        var groups = NaosGroups();
        var sel = Sel((ChestGroup, ["Chest dermals"]));
        Assert.Equal(Hidden(groups, Chest, "Right ring", ChestAttrs, sel), Hidden(groups, Chest, "right ring", ChestAttrs, sel));
        Assert.Equal([ChestGroup], ContentPieceResolver.GoverningGroups(groups, Chest, "right ring").Select(g => g.Group));
    }

    /// <summary>[EDGE-imc-group-piece-slot-7] Off a game path the set cannot be read, so a same-slot group of another set still judges it.</summary>
    [Fact]
    public void An_unparseable_path_cannot_tell_sets_apart()
    {
        List<ContentAttributeGroup> groups = [Group("Other set", "RFinger", 999, ("Show A", 1))];
        Assert.True(ContentPieceResolver.GovernsModel(groups, Chest, "Right ring"));
    }

    /// <summary>[EDGE-imc-group-piece-slot-8] A game path names its own slot, whatever the piece recorded.</summary>
    [Fact]
    public void A_game_path_wins_over_a_disagreeing_piece_slot()
    {
        const string Rir = "chara/accessory/a0103/model/c0201a0103_rir.mdl";
        Assert.Equal([ChestGroup],
            ContentPieceResolver.GoverningGroups(NaosGroups(), Rir, "Left ring").Select(g => g.Group));
    }

    /// <summary>[EDGE-imc-group-piece-slot-11] With nothing ticked each piece hides by its own group's default alone.</summary>
    [Fact]
    public void With_nothing_selected_each_ring_piece_hides_only_by_its_own_group()
    {
        var groups = NaosGroups();
        Assert.Equal(ChestAttrs, Hidden(groups, Chest, "Right ring", ChestAttrs, null));
        Assert.Equal(LegsAttrs, Hidden(groups, Legs, "Left ring", LegsAttrs, null));
        Assert.Equal("imc group " + ChestGroup, For(groups, Chest, "Right ring", ChestAttrs, null).Why);
        Assert.Equal("imc group " + LegsGroup, For(groups, Legs, "Left ring", LegsAttrs, null).Why);
    }

    /// <summary>[AC-imc-group-piece-slot-5] The log names exactly the groups whose masks were applied.</summary>
    [Fact]
    public void The_groups_named_in_the_log_are_exactly_the_groups_applied()
    {
        var groups = NaosGroups();
        var sel = Sel((ChestGroup, ["Chest dermals"]), (LegsGroup, ["Hips dermals"]));
        var cases = new (string Model, string? Slot, IReadOnlyList<string> Attrs)[]
        {
            (Chest, "Right ring", ChestAttrs),
            (Legs, "Left ring", LegsAttrs),
            (Chest, null, ChestAttrs),
            ("chara/accessory/a0103/model/c0201a0103_ril.mdl", null, LegsAttrs),
        };
        var excludedMattered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (model, slot, attrs) in cases)
        {
            var v = For(groups, model, slot, attrs, sel);
            Assert.Equal(ImcMaskSource.ImcGroup, v.Source);
            Assert.StartsWith("imc group ", v.Why);
            var named = v.Why!["imc group ".Length..].Split(", ");
            Assert.Equal(ContentPieceResolver.GoverningGroups(groups, model, slot).Select(g => g.Group), named);

            var applied = groups.Where(g => named.Contains(g.Group)).ToList();
            Assert.Equal(Hidden(applied, model, null, attrs, sel), (v.Hidden ?? new HashSet<string>()).Order());
            foreach (var other in groups.Except(applied))
                if (!Hidden([.. applied, other], model, null, attrs, sel).SequenceEqual(Hidden(applied, model, null, attrs, sel)))
                    excludedMattered.Add(other.Group);
        }
        // Each group left out somewhere would have changed that piece: leaving it out was a decision, not a no-op.
        Assert.Equal([ChestGroup, LegsGroup], excludedMattered.Order());
    }

    /// <summary>
    /// [AC-imc-group-piece-slot-6] [EDGE-imc-group-piece-slot-6] A piece off a game path whose slot no group names shows
    /// every part with its variant tags off, so the host's own mask cannot judge them; at a game path, the old flow.
    /// </summary>
    [Fact]
    public void A_disk_path_piece_whose_slot_no_group_names_shows_every_part_untagged()
    {
        var groups = NaosGroups();
        string[] attrs = ["atr_nv_a", "atr_rv_a", "atr_ude"];
        var v = For(groups, "neck/necklace.mdl", "Necklace", attrs, AllOf(groups));
        Assert.Null(v.Hidden);
        Assert.False(v.OwnAttributes);
        Assert.True(v.DropVariantAttributes);
        Assert.Equal(ImcMaskSource.AllParts, v.Source);
        Assert.Equal("all parts (no imc group for Necklace)", v.Why);
        Assert.False(ContentPieceResolver.GovernsModel(groups, "neck/necklace.mdl", "Necklace"));

        var atGamePath = For(groups, "chara/accessory/a0103/model/c0201a0103_nek.mdl", "Necklace", attrs, AllOf(groups));
        Assert.Equal(default, atGamePath);
    }

    /// <summary>
    /// [AC-imc-group-piece-slot-7] [EDGE-imc-group-piece-slot-2] [EDGE-imc-group-piece-slot-3]
    /// [EDGE-imc-group-piece-slot-8] [EDGE-imc-group-piece-slot-13] [EDGE-imc-group-piece-slot-14]
    /// When a piece's slot cannot be trusted, and why.
    /// </summary>
    [Theory]
    [InlineData("chara/accessory/a0103/model/c0201a0103_rir.mdl", "Left ring", "naos", "PathDisagrees")]
    [InlineData("chara/accessory/a0103/model/c0201a0103_rir.mdl", "Right ring", "naos", "None")]
    [InlineData("chara/accessory/a0103/model/c0201a0103_nek.mdl", "Right ring", "naos", "PathDisagrees")]
    [InlineData(Chest, null, "naos", "MissingSlot")]
    [InlineData(Chest, "  ", "naos", "MissingSlot")]
    [InlineData(Chest, "Hair", "naos", "UnusableSlot")]
    [InlineData(Chest, "Body (dress)", "naos", "UnusableSlot")]
    [InlineData(Chest, "Right ring", "naos", "None")]
    [InlineData(Chest, null, "none", "None")]
    [InlineData("chara/accessory/a0103/model/c0201a0103_rir.mdl", "Left ring", "none", "None")]
    [InlineData(Chest, null, "slotless", "None")]
    [InlineData("chara/accessory/a0103/model/c0201a0103_rir.mdl", "Left ring", "slotless", "None")]
    public void SlotNotice_says_when_a_pieces_slot_cannot_be_trusted(
        string model, string? slot, string groups, string expected)
    {
        List<ContentAttributeGroup>? g = groups switch
        {
            "naos" => NaosGroups(),
            "slotless" => [Group("A", null, 103, ("x", 1)), Group("B", "Glasses", 103, ("y", 2))],
            _ => null,
        };
        Assert.Equal(Enum.Parse<PieceSlotNotice>(expected), ContentPieceResolver.SlotNotice(g, model, slot));
    }

    /// <summary>
    /// [AC-imc-group-piece-slot-7] [EDGE-imc-group-piece-slot-8] [EDGE-imc-group-piece-slot-14]
    /// [EDGE-imc-group-piece-slot-15] Every notice's wording, placeholders in place.
    /// </summary>
    [Fact]
    public void SlotNoticeMessage_pins_every_format()
    {
        const string Mod = "Mod {x}";
        const string Rir = "chara/accessory/a0103/model/c0201a0103_rir.mdl";
        const string Nek = "chara/accessory/a0103/model/c0201a0103_nek.mdl";

        Assert.Equal($"[Proteus] content: Mod {{x}} — {Rir} is named for the Right ring slot but was imported as Left ring; "
                     + "its toggles follow Chest piercings, Extra",
            ContentPieceResolver.SlotNoticeMessage(PieceSlotNotice.PathDisagrees, Mod, Rir, "Right ring", "Left ring",
                [ChestGroup, "Extra"]));
        Assert.Equal($"[Proteus] content: Mod {{x}} — {Nek} is named for the Necklace slot but was imported as Right ring; "
                     + "it is judged as Necklace",
            ContentPieceResolver.SlotNoticeMessage(PieceSlotNotice.PathDisagrees, Mod, Nek, "Necklace", "Right ring", []));
        Assert.Equal("[Proteus] content: Mod {x} — chest/x.mdl has no recorded slot, so every one of its toggle groups "
                     + "judges it; re-import the mod to record it",
            ContentPieceResolver.SlotNoticeMessage(PieceSlotNotice.MissingSlot, Mod, "chest/x.mdl", null, null, []));
        Assert.Equal("[Proteus] content: Mod {x} — chest/x.mdl's slot \"Body (dress)\" can't pick its toggle group, so "
                     + "every one of its toggle groups judges it",
            ContentPieceResolver.SlotNoticeMessage(PieceSlotNotice.UnusableSlot, Mod, "chest/x.mdl", null, "Body (dress)", []));
        Assert.Null(ContentPieceResolver.SlotNoticeMessage(PieceSlotNotice.None, Mod, Rir, "Right ring", "Right ring", []));
    }

    /// <summary>[AC-imc-group-piece-slot-7] [EDGE-imc-group-piece-slot-12] Each notice is said once per mod, independently.</summary>
    [Fact]
    public void Slot_notices_fire_once_per_mod()
    {
        var a = "slot-notice-" + Guid.NewGuid().ToString("N");
        var b = "slot-notice-" + Guid.NewGuid().ToString("N");

        Assert.True(ContentImcNotices.FirstSlotDisagreement(a));
        Assert.False(ContentImcNotices.FirstSlotDisagreement(a));
        Assert.True(ContentImcNotices.FirstUntrustedSlot(a));   // the other gate is still open
        Assert.False(ContentImcNotices.FirstUntrustedSlot(a));
        Assert.True(ContentImcNotices.FirstUntrustedSlot(b));   // and another mod has its own
        Assert.True(ContentImcNotices.FirstSlotDisagreement(b));
    }

    /// <summary>[AC-imc-group-piece-slot-3] The composite hands the piece's recorded slot to the visibility rule.</summary>
    [Fact]
    public void Wiring_content_resolution_passes_the_pieces_slot_to_the_visibility_rule()
    {
        var src = SourceText.Read("Proteus/Services/SecondSkinService.ContentResolution.cs");
        int at = src.IndexOf("ContentImcVisibility.For(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var call = string.Join(" ", src[at..(at + 400)].Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("modelRel, piece.Slot, parsedModel.Attrs,", call);
    }

    /// <summary>[AC-imc-group-piece-slot-7] [EDGE-imc-group-piece-slot-15] The notices are logged once per mod, verbatim.</summary>
    [Fact]
    public void Wiring_content_resolution_logs_slot_notices_once_per_mod()
    {
        var src = SourceText.Read("Proteus/Services/SecondSkinService.ContentResolution.cs");
        Assert.Contains("SlotNotice(cEntry.Metadata.ContentAttributes, modelRel, piece.Slot)", src);
        int at = src.IndexOf("PieceSlotNotice.PathDisagrees", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var block = src[at..(at + 700)];
        Assert.Contains("ContentImcNotices.FirstSlotDisagreement(modRoot)", block);
        Assert.Contains("ContentImcNotices.FirstUntrustedSlot(modRoot)", block);
        Assert.Contains("Information(\"{0}\", SlotNoticeMessage(", block);
        Assert.Contains("GoverningGroups(cEntry.Metadata.ContentAttributes, modelRel, piece.Slot)", block);
    }
}
