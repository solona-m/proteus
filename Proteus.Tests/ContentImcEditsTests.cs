using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Proteus.Services;
using Xunit;
using static Proteus.Services.PenumbraPackage;
using static Proteus.Tests.SyntheticPack;

namespace Proteus.Tests;

/// <summary>
/// The IMC part toggles a pack carries inside ordinary options and default data (<see cref="ContentImcEdits"/>), and
/// how they decide which parts of an imported piece are drawn (<see cref="ContentImcVisibility"/>).
/// <para/>
/// An imported piece rides a host accessory, so the game reads the host's IMC mask, never the item the pack edits.
/// Before these were read, a TexTools-converted pack's toggles did nothing at all once imported.
/// </summary>
public class ContentImcEditsTests
{
    private const string Top = "base/chara/equipment/e6100/model/c0201e6100_top.mdl";
    private static readonly ImcItem Body = new('e', 6100, "Body");
    private static readonly string[] Attrs = ["atr_tv_a", "atr_tv_b", "atr_dv_a", "atr_ude"];

    // ── fixtures ────────────────────────────────────────────────────────────

    private static PackImc Imc(int mask, int variant = 1, int set = 6100, string slot = "Body",
                               string type = "Equipment")
        => new(type, set, variant, slot, 1, 0, 0, 0, mask, 0);

    private static PackOption Opt(string name, params PackImc[] imc) => OptP(name, 0, imc);

    private static PackOption OptP(string name, int priority, params PackImc[] imc)
        => new(name, null, new Dictionary<string, string>(), [], 0, null, null,
               [.. imc.Select(i => new PackManipulation("Imc", i.PrimaryId))], imc, priority);

    private static PackGroup Grp(string name, string type, int index, params PackOption[] opts)
        => GrpP(name, type, index, 0, 0, opts);

    private static PackGroup GrpP(string name, string type, int index, int priority, ulong defaults,
                                  params PackOption[] opts)
        => new(name, type, index, opts, null, Priority: priority, DefaultSettings: defaults);

    private static Contents Pack(PackImc[]? defaults, params PackGroup[] groups)
        => PackWith(new Dictionary<string, string>(), defaults, groups);

    private static Contents PackWith(Dictionary<string, string> files, PackImc[]? defaults, params PackGroup[] groups)
        => new("synthetic", 4, "n", "a", null, null, null, new Dictionary<string, long>(), files, groups,
               null, defaults);

    private static Dictionary<string, List<string>> Sel(string group, params string[] on)
        => new(StringComparer.Ordinal) { [group] = [.. on] };

    private static ContentVisibility For(ContentImcEdits? edits, IReadOnlyDictionary<string, List<string>>? selected,
                                         ushort? vanilla = null, IReadOnlyList<ContentAttributeGroup>? groups = null,
                                         string model = Top, IReadOnlyList<string>? attrs = null)
        => ContentImcVisibility.For(groups, edits, model, attrs ?? Attrs, selected, (_, _) => vanilla);

    /// <summary>The one-group Single "Belt" toggle: On draws both body parts, Off leaves part B off.</summary>
    private static ContentImcEdits BeltToggle()
        => ContentImcEdits.From(Pack(null, Grp("Belt", "Single", 0, Opt("On", Imc(0x3FF)), Opt("Off", Imc(0x3FD)))))!;

    // ── AC-1: options switch parts ──────────────────────────────────────────

    /// <summary>
    /// [AC-imported-imc-toggles-1] [EDGE-imported-imc-toggles-36] The option chosen in Penumbra decides the parts, and
    /// with every part shown the variant tags still come off, or the host's own mask would judge them again.
    /// </summary>
    [Fact]
    public void Changing_an_option_with_an_imc_edit_changes_the_hidden_parts()
    {
        using var built = SyntheticPack.ImcInOptions(
            [new("Belt", "Single", 0, 0, O("On", 0x3FF), O("Off", 0x3FD))]);
        var edits = ContentImcEdits.From(Read(built.Path));

        var off = For(edits, Sel("Belt", "Off"));
        Assert.Equal(["atr_tv_b"], off.Hidden!.Order());
        Assert.True(off.DropVariantAttributes);
        Assert.False(off.OwnAttributes);

        var on = For(edits, Sel("Belt", "On"));
        Assert.Null(on.Hidden);
        Assert.True(on.DropVariantAttributes);
        Assert.False(on.OwnAttributes);
        Assert.Equal("option Belt/On", on.Why);
    }

    /// <summary>[AC-imported-imc-toggles-2] Default data's mask applies with no option touched.</summary>
    [Fact]
    public void Default_data_imc_governs_a_garment_with_no_option_touched()
    {
        var edits = ContentImcEdits.From(Pack([Imc(0x3FE)]));
        foreach (var sel in new[] { new Dictionary<string, List<string>>(), null })
        {
            var v = For(edits, sel, vanilla: 0x3FF);
            Assert.Equal(["atr_tv_a"], v.Hidden!.Order());
            Assert.Equal("default data", v.Why);
        }
    }

    // ── AC-3: Penumbra's order ──────────────────────────────────────────────

    /// <summary>[AC-imported-imc-toggles-3] The higher-priority group's edit is the one Penumbra applies.</summary>
    [Fact]
    public void Higher_group_priority_wins_the_same_imc_entry()
    {
        var edits = ContentImcEdits.From(Pack(null,
            GrpP("High", "Single", 0, 5, 0, Opt("x", Imc(1))),
            GrpP("Low", "Single", 1, 0, 0, Opt("y", Imc(2)))))!;
        Assert.Equal((ushort)1, edits.Active(Body, null)!.Mask);
    }

    /// <summary>[AC-imported-imc-toggles-3] On a priority tie the LATER group wins (Penumbra <c>Mod.cs:145</c> reverses before sorting).</summary>
    [Fact]
    public void On_equal_priority_the_later_group_wins()
    {
        var edits = ContentImcEdits.From(Pack(null,
            Grp("First", "Single", 0, Opt("x", Imc(1))),
            Grp("Second", "Single", 1, Opt("y", Imc(2)))))!;
        Assert.Equal("Second", edits.Active(Body, null)!.Group);
    }

    /// <summary>[AC-imported-imc-toggles-3] Inside a Multi group: option priority first, then the earlier option.</summary>
    [Fact]
    public void Multi_options_apply_by_option_priority_then_earlier_index()
    {
        var tie = ContentImcEdits.From(Pack(null,
            Grp("M", "Multi", 0, Opt("a", Imc(1)), Opt("b", Imc(2)))))!;
        Assert.Equal("a", tie.Active(Body, Sel("M", "a", "b"))!.Option);

        var ranked = ContentImcEdits.From(Pack(null,
            Grp("M", "Multi", 0, OptP("a", 0, Imc(1)), OptP("b", 3, Imc(2)))))!;
        Assert.Equal("b", ranked.Active(Body, Sel("M", "a", "b"))!.Option);
    }

    /// <summary>[AC-imported-imc-toggles-3] Default data applies last, so any active option beats it.</summary>
    [Fact]
    public void Default_data_loses_to_any_active_option()
    {
        var edits = ContentImcEdits.From(Pack([Imc(1)], Grp("M", "Multi", 0, Opt("a", Imc(2)))))!;
        Assert.Equal((ushort)2, edits.Active(Body, Sel("M", "a"))!.Mask);
        Assert.Equal((ushort)1, edits.Active(Body, Sel("M"))!.Mask);
    }

    // ── AC-4: the game's own entry ──────────────────────────────────────────

    /// <summary>[AC-imported-imc-toggles-4] No active edit: the game's entry for the governing variant, else every part.</summary>
    [Fact]
    public void No_active_edit_falls_back_to_the_games_entry_at_the_governing_variant()
    {
        var edits = ContentImcEdits.From(Pack(null, Grp("M", "Multi", 0, Opt("a", Imc(0x3FF, variant: 3)))))!;
        int asked = -1;
        var v = ContentImcVisibility.For(null, edits, Top, Attrs, Sel("M"), (_, variant) => { asked = variant; return 1; });
        Assert.Equal(edits.GoverningVariant(Body), asked);
        Assert.Equal(3, asked);
        Assert.Equal(["atr_tv_b"], v.Hidden!.Order());
        Assert.Equal("game entry", v.Why);

        var none = For(edits, Sel("M"), vanilla: null);
        Assert.Null(none.Hidden);
        Assert.Equal("all parts", none.Why);
        Assert.True(none.DropVariantAttributes);
    }

    // ── AC-6 / AC-7: old imports, Imc groups ────────────────────────────────

    /// <summary>[AC-imported-imc-toggles-6] A mod imported before this build is read straight off its files, which stay as they were.</summary>
    [Fact]
    public void A_mod_imported_before_this_build_is_governed_without_rewriting()
    {
        using var built = SyntheticPack.ImcInOptions([new("Belt", "Single", 0, 1, O("On", 0x3FF), O("Off", 0x3FD))]);
        var root = Path.Combine(Path.GetTempPath(), "proteus-imc-" + Guid.NewGuid().ToString("N"));
        try
        {
            ContentImportService.WriteMod(root, "Old", "Tests", ContentImportService.Inspect(built.Path));
            // The sidecar as an older build wrote it: no record of these toggles at all.
            var sidecar = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json");
            Assert.Null(SidecarDiscoveryService.TryReadMetadata(root)!.ContentAttributes);
            var before = (File.ReadAllBytes(sidecar), File.ReadAllBytes(Path.Combine(root, "meta.json")));

            var (edits, error) = ContentImcEdits.ReadInstalled(root);
            Assert.Null(error);
            // DefaultSettings 1 = "Off".
            Assert.Equal(["atr_tv_b"], For(edits, null).Hidden!.Order());

            Assert.Equal(before.Item1, File.ReadAllBytes(sidecar));
            Assert.Equal(before.Item2, File.ReadAllBytes(Path.Combine(root, "meta.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>
    /// [AC-imported-imc-toggles-7] A model an Imc group governs resolves exactly as before — same hidden names, tags
    /// stripped — whether or not option edits name its item too.
    /// </summary>
    [Fact]
    public void An_imc_group_governed_model_resolves_exactly_as_before()
    {
        const string Mid = "items/chara/equipment/e6058/model/c0101e6058_dwn.mdl";
        List<ContentAttributeGroup> groups =
        [
            new() { Group = "Toggles", SetId = 6058, DefaultMask = 3,
                    Options = new(StringComparer.Ordinal) { ["Panty Strap Hide"] = 1, ["Pockets Hide"] = 2 } },
        ];
        string[] attrs = ["atr_dv_a", "atr_dv_b"];
        var options = ContentImcEdits.From(Pack(null,
            Grp("Other", "Single", 0, Opt("x", Imc(0, set: 6058, slot: "Legs")))));

        foreach (var sel in new[] { null, Sel("Toggles"), Sel("Toggles", "Panty Strap Hide"),
                                    Sel("Toggles", "Panty Strap Hide", "Pockets Hide") })
            foreach (var edits in new[] { null, options })
            {
                var v = ContentImcVisibility.For(groups, edits, Mid, attrs, sel, (_, _) => 0);
                var legacy = ContentPieceResolver.HiddenAttributes(groups, Mid, attrs, sel);
                Assert.Equal(legacy?.Order(), v.Hidden?.Order());
                Assert.True(v.OwnAttributes);
                Assert.False(v.DropVariantAttributes);
            }
    }

    // ── AC-8 wiring ─────────────────────────────────────────────────────────

    /// <summary>[AC-imported-imc-toggles-8] [EDGE-imported-imc-toggles-32] Finish copies the source's persistent settings into the import selection.</summary>
    [Fact]
    public void Wiring_finish_copies_the_sources_persistent_settings()
    {
        var finish = SourceText.Method("Proteus/Services/ContentImportService.cs", "private ImportResult Finish(");
        Assert.Contains("InstalledSourceDir(", finish);
        Assert.Contains("GetCollectionModSettings(collId.Value, ignoreTemporary: true)", finish);
        Assert.Contains("ApplyImportSelection(collId.Value, dirName, preview, sourceSelection)", finish);
        var apply = SourceText.Method("Proteus/Services/ContentImportService.cs", "private void ApplyImportSelection(");
        Assert.Contains("ImportSelection(preview, sourceSelection)", apply);
    }

    // ── edge cases ──────────────────────────────────────────────────────────

    /// <summary>[EDGE-imported-imc-toggles-1] The material-folder variant governs when written; else the lowest from 1; else 0.</summary>
    [Fact]
    public void The_material_variant_governs_then_lowest_written_then_zero()
    {
        var material = new Dictionary<string, string>
            { ["chara/equipment/e6100/material/v0003/mt_c0201e6100_top_a.mtrl"] = "m.mtrl" };
        var g = Grp("M", "Multi", 0, Opt("a", Imc(1, variant: 2), Imc(2, variant: 3), Imc(4, variant: 0)));

        var byMaterial = ContentImcEdits.From(PackWith(material, null, g))!;
        Assert.Equal(3, byMaterial.GoverningVariant(Body));
        Assert.Equal((ushort)2, byMaterial.Active(Body, Sel("M", "a"))!.Mask);

        Assert.Equal(2, ContentImcEdits.From(Pack(null, g))!.GoverningVariant(Body));
        Assert.Equal(0, ContentImcEdits.From(Pack(null, Grp("M", "Multi", 0, Opt("a", Imc(4, variant: 0)))))!
            .GoverningVariant(Body));
    }

    /// <summary>[EDGE-imported-imc-toggles-2] An accessory edit does not reach equipment of the same number.</summary>
    [Fact]
    public void An_accessory_edit_does_not_govern_equipment_with_the_same_set_number()
    {
        var edits = ContentImcEdits.From(Pack([Imc(0, type: "Accessory", slot: "Wrists")]))!;
        Assert.False(edits.Names(Body));
        Assert.True(edits.Names(new ImcItem('a', 6100, "Bracelets")));
        Assert.Equal(default, For(edits, null));
    }

    /// <summary>[EDGE-imported-imc-toggles-3] Two garments on one set: each edit governs its own slot only.</summary>
    [Fact]
    public void An_edit_governs_only_its_own_slot_on_a_shared_set()
    {
        var edits = ContentImcEdits.From(Pack([Imc(0, slot: "Legs")]))!;
        Assert.False(edits.Names(Body));
        Assert.Equal(default, For(edits, null));
    }

    /// <summary>[EDGE-imported-imc-toggles-4] An Imc group outranks option edits for its own item, and only that one.</summary>
    [Fact]
    public void An_imc_group_outranks_option_edits_for_its_item_only()
    {
        List<ContentAttributeGroup> groups =
            [new() { Group = "Legs", SetId = 6100, Slot = "Legs", DefaultMask = 0x3FF }];
        var edits = ContentImcEdits.From(Pack([Imc(0, slot: "Legs"), Imc(0x3FD)]))!;

        var legs = For(edits, null, groups: groups, model: "base/chara/equipment/e6100/model/c0201e6100_dwn.mdl",
                       attrs: ["atr_dv_a", "atr_dv_b"]);
        Assert.True(legs.OwnAttributes);
        Assert.Null(legs.Hidden);

        var top = For(edits, null, groups: groups);
        Assert.False(top.OwnAttributes);
        Assert.Equal(["atr_tv_b"], top.Hidden!.Order());
    }

    /// <summary>[EDGE-imported-imc-toggles-5] A legacy record naming no set and no slot keeps every model on the legacy path.</summary>
    [Fact]
    public void A_slotless_legacy_record_keeps_the_whole_mod_on_the_legacy_path()
    {
        List<ContentAttributeGroup> groups = [new() { Group = "Any", DefaultMask = 0x3FF }];
        var v = For(ContentImcEdits.From(Pack([Imc(0)])), null, groups: groups);
        Assert.True(v.OwnAttributes);
        Assert.False(v.DropVariantAttributes);
    }

    /// <summary>[EDGE-imported-imc-toggles-6] With no selection to read, each group's own default decides.</summary>
    [Fact]
    public void Without_a_selection_the_manifest_defaults_decide()
    {
        var single = ContentImcEdits.From(Pack(null,
            GrpP("S", "Single", 0, 0, 1, Opt("a", Imc(1)), Opt("b", Imc(2)))))!;
        Assert.Equal("b", single.Active(Body, null)!.Option);
        // A Single group always has a choice: reported with none, its default stands rather than every option off.
        Assert.Equal("b", single.Active(Body, Sel("S"))!.Option);

        var multi = ContentImcEdits.From(Pack(null,
            GrpP("M", "Multi", 0, 0, 0b10, Opt("a", Imc(1)), Opt("b", Imc(2)))))!;
        Assert.Equal("b", multi.Active(Body, null)!.Option);
    }

    /// <summary>[EDGE-imported-imc-toggles-7] A missing group reads its default; an unknown option adds nothing; case does not matter.</summary>
    [Fact]
    public void A_missing_group_uses_its_default_and_an_unknown_option_adds_nothing()
    {
        var edits = ContentImcEdits.From(Pack(null,
            GrpP("M", "Multi", 0, 0, 0b1, Opt("a", Imc(1)), Opt("b", Imc(2)))))!;
        Assert.Equal("a", edits.Active(Body, Sel("Elsewhere", "x"))!.Option);
        Assert.Null(edits.Active(Body, Sel("M", "nope")));
        Assert.Equal("b", edits.Active(Body, Sel("m", "B"))!.Option);
    }

    /// <summary>[EDGE-imported-imc-toggles-8] A model whose entry name is not a gear file name is not governed.</summary>
    [Fact]
    public void An_unparseable_model_entry_is_not_governed()
    {
        var edits = ContentImcEdits.From(Pack([Imc(0)]));
        Assert.Null(ContentImcEdits.ItemOf("model.mdl"));
        Assert.Equal(default, For(edits, null, model: "model.mdl"));
    }

    /// <summary>[EDGE-imported-imc-toggles-9] Imc and Combining groups are not option edits.</summary>
    [Fact]
    public void Imc_and_combining_groups_are_not_option_edits()
        => Assert.Null(ContentImcEdits.From(Pack(null,
               Grp("I", "Imc", 0, Opt("a", Imc(1))), Grp("C", "Combining", 1, Opt("b", Imc(2))))));

    /// <summary>[EDGE-imported-imc-toggles-10] An unreadable manifest is reported, and said once per mod per session.</summary>
    [Fact]
    public void A_read_failure_is_announced_once_per_mod_per_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus-imc-" + Guid.NewGuid().ToString("N"));
        var other = root + "-other";
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "meta.json"), "{ not json");
            var (edits, error) = ContentImcEdits.ReadInstalled(root);
            Assert.Null(edits);
            Assert.NotNull(error);
            Assert.True(ContentImcNotices.FirstReadFailure(root));
            Assert.False(ContentImcNotices.FirstReadFailure(root));
            Assert.True(ContentImcNotices.FirstReadFailure(other));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-10] The composite warns once, then logs at Debug, and carries on.</summary>
    [Fact]
    public void Wiring_a_failed_manifest_read_warns_once_then_debugs()
    {
        var m = SourceText.Method("Proteus/Services/SecondSkinService.ContentResolution.cs", "private ContentImcEdits? ImcEdits(");
        Assert.Contains("ContentImcEdits.ReadInstalled(modRoot)", m);
        Assert.Contains("if (ContentImcNotices.FirstReadFailure(modRoot))", m);
        Assert.Contains("log.Warning(", m);
        Assert.Contains("log.Debug(", m);
    }

    /// <summary>[EDGE-imported-imc-toggles-12] An edit for an item no piece is governs no piece.</summary>
    [Fact]
    public void An_edit_for_an_item_no_piece_is_governs_nothing()
    {
        var edits = ContentImcEdits.From(Pack([Imc(0, set: 42)]))!;
        Assert.False(edits.Names(Body));
        Assert.Equal(default, For(edits, null));
    }

    /// <summary>[EDGE-imported-imc-toggles-17] Only the slot's own variant parts are hidden; atr_ude, bare letters and custom names never are.</summary>
    [Fact]
    public void Only_the_slots_variant_attributes_are_hidden_by_an_option_mask()
    {
        var v = For(ContentImcEdits.From(Pack([Imc(0)])), null,
                    attrs: ["atr_tv_a", "atr_ude", "atr_a", "custom_name", "atr_hij"]);
        Assert.Equal(["atr_tv_a"], v.Hidden!.Order());
    }

    /// <summary>
    /// [EDGE-imported-imc-toggles-17] The slot-letter table agrees with the Parts tab's and the body refit's, and the
    /// hiding rule with the body refit's. Reached by reflection: neither file is this change's to edit.
    /// </summary>
    [Fact]
    public void Variant_letters_match_the_parts_tab_and_body_retarget()
    {
        var asm = typeof(ContentSlot).Assembly;
        var parts = asm.GetType("Proteus.Services.MeshToggleService")!
            .GetMethod("AttributeSlotLetter", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("MeshToggleService.AttributeSlotLetter");
        foreach (var label in new[] { "Head", "Body", "Hands", "Legs", "Feet", "Earrings", "Necklace", "Bracelets",
                                      "Right ring", "Left ring", "Hair", "Face" })
            Assert.Equal((char?)parts.Invoke(null, [label]), ContentSlot.VariantLetter(label));

        var retarget = asm.GetType("Proteus.Services.BodyRetarget")!;
        var letter = retarget.GetMethod("VariantLetter", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("BodyRetarget.VariantLetter");
        var undrawn = retarget.GetMethod("UndrawnVariants", BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] attrs = ["atr_tv_a", "atr_tv_b", "atr_dv_a", "atr_gv_c", "atr_sv_a", "atr_ude"];
        foreach (var (suffix, label) in new[] { ("_top", "Body"), ("_dwn", "Legs"), ("_glv", "Hands"), ("_sho", "Feet") })
        {
            Assert.Equal((char?)letter.Invoke(null, [suffix]), ContentSlot.VariantLetter(label));
            var theirs = (IReadOnlySet<string>)undrawn.Invoke(null, [attrs, suffix, (ushort)0b10])!;
            Assert.Equal(theirs.Order(),
                ContentImcEdits.HiddenVariants(attrs, ContentSlot.VariantLetter(label)!.Value, 0b10).Order());
        }
    }

    /// <summary>[EDGE-imported-imc-toggles-18] Bits past the model's variant parts change nothing.</summary>
    [Fact]
    public void Bits_past_the_models_variant_parts_change_nothing()
        => Assert.Null(For(ContentImcEdits.From(Pack([Imc(0b11)])), null, attrs: ["atr_tv_a", "atr_tv_b"]).Hidden);

    /// <summary>[EDGE-imported-imc-toggles-19] Unknown slots and weapon edits are ignored.</summary>
    [Fact]
    public void Unknown_slots_and_weapon_edits_are_ignored()
        => Assert.Null(ContentImcEdits.From(Pack([Imc(0, slot: "Nowhere"), Imc(0, type: "Weapon")])));

    /// <summary>[EDGE-imported-imc-toggles-20] A setting change on a sidecar mod forces a recomposite, even for an option with no piece.</summary>
    [Fact]
    public void Wiring_a_setting_change_on_a_sidecar_mod_forces_a_recomposite()
    {
        var events = SourceText.Read("Proteus/Services/CompositorService.Events.cs");
        int sidecar = events.IndexOf("if (sidecar)", StringComparison.Ordinal);
        Assert.True(sidecar >= 0);
        Assert.Contains("TriggerRecomposite($\"ModSettingChanged:{change}:{modDir}\", force: !ambient",
                        events[sidecar..]);
    }

    /// <summary>[EDGE-imported-imc-toggles-20] The composite feeds the live selection into the rule and the result into the geometry.</summary>
    [Fact]
    public void Wiring_content_resolution_feeds_the_live_selection_to_the_visibility_rule()
    {
        var src = SourceText.Read("Proteus/Services/SecondSkinService.ContentResolution.cs");
        int at = src.IndexOf("ContentImcVisibility.For(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("ModSelection(cEntry.ModDirectory)", src[at..(at + 600)]);
        Assert.Contains("DropVariantAttributes: vis.DropVariantAttributes", src);
        Assert.Contains("OwnAttributes: vis.OwnAttributes", src);
    }

    /// <summary>[EDGE-imported-imc-toggles-21] An authored Proteus mod's option edits govern its pieces too.</summary>
    [Fact]
    public void An_authored_proteus_mods_option_edits_govern_its_pieces()
    {
        // The composite does not ask how the mod came to be: the same reading applies to a pack built as a Proteus mod.
        var v = For(BeltToggle(), Sel("Belt", "Off"));
        Assert.Equal(["atr_tv_b"], v.Hidden!.Order());
    }

    /// <summary>[EDGE-imported-imc-toggles-22] A chosen option with no edit falls through to default data, then the game.</summary>
    [Fact]
    public void A_single_option_without_an_edit_falls_through_to_default_then_game()
    {
        var withDefault = ContentImcEdits.From(Pack([Imc(0x3FE)],
            Grp("S", "Single", 0, Opt("plain"), Opt("edit", Imc(0x3FD)))))!;
        Assert.Equal("default data", For(withDefault, Sel("S", "plain")).Why);

        var noDefault = ContentImcEdits.From(Pack(null, Grp("S", "Single", 0, Opt("plain"), Opt("edit", Imc(0x3FD)))));
        Assert.Equal("game entry", For(noDefault, Sel("S", "plain"), vanilla: 0x3FF).Why);
    }

    /// <summary>[EDGE-imported-imc-toggles-24] Another slot's variant tag is never hidden by this item's mask.</summary>
    [Fact]
    public void Another_slots_variant_tag_is_never_hidden_by_this_items_mask()
    {
        var v = For(ContentImcEdits.From(Pack([Imc(0)])), null, attrs: ["atr_dv_a", "atr_tv_a"]);
        Assert.Equal(["atr_tv_a"], v.Hidden!.Order());
    }

    /// <summary>[EDGE-imported-imc-toggles-25] An old import's zeroed toggle now takes the game's entry.</summary>
    [Fact]
    public void An_old_import_with_its_imc_option_zeroed_now_takes_the_vanilla_mask()
    {
        var edits = ContentImcEdits.From(Pack(null, GrpP("Parts", "Multi", 0, 0, 0, Opt("Show belt", Imc(0x3FF)))));
        var v = For(edits, Sel("Parts"), vanilla: 0x3FD);
        Assert.Equal("game entry", v.Why);
        Assert.Equal(["atr_tv_b"], v.Hidden!.Order());
    }

    /// <summary>[EDGE-imported-imc-toggles-25] The hint names the options whose mask shows the hidden parts again.</summary>
    [Fact]
    public void OptionsThatShow_names_the_options_whose_mask_shows_the_hidden_parts()
    {
        var edits = ContentImcEdits.From(Pack([Imc(0x3FF)], Grp("Parts", "Multi", 0,
            Opt("Show belt", Imc(0b10)), Opt("Hide belt", Imc(0b01)), Opt("Other variant", Imc(0b10, variant: 2)))))!;
        Assert.Equal(["Parts / Show belt"], edits.OptionsThatShow(Body, ["atr_tv_b"]));
    }

    /// <summary>[EDGE-imported-imc-toggles-25] The hint is logged once per mod per session, and only for the game's entry.</summary>
    [Fact]
    public void The_game_entry_hint_is_logged_once_per_mod_per_session()
    {
        var root = "proteus-hint-" + Guid.NewGuid().ToString("N");
        Assert.True(ContentImcNotices.FirstGameEntryHint(root));
        Assert.False(ContentImcNotices.FirstGameEntryHint(root));

        var src = SourceText.Read("Proteus/Services/SecondSkinService.ContentResolution.cs");
        int at = src.IndexOf("ContentImcNotices.FirstGameEntryHint(modRoot)", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("vis.Source == ImcMaskSource.GameEntry", src[Math.Max(0, at - 400)..at]);
    }

    /// <summary>[EDGE-imported-imc-toggles-26] TexTools writes variants from 0; variant 1 governs, not the default set.</summary>
    [Fact]
    public void Texttools_variants_from_zero_govern_by_variant_one()
    {
        using var built = SyntheticPack.ImcInOptions(
            [new("Belt", "Single", 0, 0, O("Off", 0x3FF, Variants: [0, 1, 2], VariantOneMask: 0x3FD))]);
        var edits = ContentImcEdits.From(Read(built.Path))!;
        Assert.Equal(1, edits.GoverningVariant(Body));
        Assert.Equal(["atr_tv_b"], For(edits, Sel("Belt", "Off")).Hidden!.Order());
    }

    /// <summary>[EDGE-imported-imc-toggles-27] A Parts-tab switch on the item takes it onto the legacy path; the option edits stop counting.</summary>
    [Fact]
    public void A_parts_tab_group_takes_an_option_governed_item_onto_the_legacy_path()
    {
        var edits = BeltToggle();
        Assert.True(For(edits, Sel("Belt", "Off")).DropVariantAttributes);

        List<ContentAttributeGroup> partsTab = [new() { Group = "Switches", SetId = 6100, Slot = "Body", DefaultMask = 0x3FF }];
        var v = For(edits, Sel("Belt", "Off"), groups: partsTab);
        Assert.True(v.OwnAttributes);
        Assert.Null(v.Hidden);
    }

    /// <summary>[EDGE-imported-imc-toggles-28] The manifest is read through a per-build cache only — nothing kept across builds.</summary>
    [Fact]
    public void Wiring_the_manifest_is_read_once_per_mod_per_build()
    {
        var src = SourceText.Read("Proteus/Services/SecondSkinService.ContentResolution.cs");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"ContentImcEdits\.ReadInstalled\("));
        Assert.Contains("imcEditsCache = new Dictionary<string, ContentImcEdits?>", src);
        // No cache of parsed edits outlives a build: only the "said it once" sets are static.
        Assert.DoesNotContain("ConcurrentDictionary<string, ContentImcEdits", SourceText.Read("Proteus/Services/ContentImcEdits.cs"));
    }

    /// <summary>[EDGE-imported-imc-toggles-28] A rewritten manifest is seen at once.</summary>
    [Fact]
    public void ReadInstalled_sees_a_rewritten_manifest_immediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus-imc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Meta(int mask) => "{\"FileVersion\":4,\"Name\":\"x\",\"DefaultData\":{\"Manipulations\":[{\"Type\":\"Imc\","
                + "\"Manipulation\":{\"Entry\":{\"AttributeMask\":" + mask + "},\"ObjectType\":\"Equipment\","
                + "\"PrimaryId\":6100,\"Variant\":1,\"EquipSlot\":\"Body\"}}]},\"Groups\":[]}";
            File.WriteAllText(Path.Combine(root, "meta.json"), Meta(1022));
            Assert.Equal((ushort)1022, ContentImcEdits.ReadInstalled(root).Edits!.Active(Body, null)!.Mask);
            File.WriteAllText(Path.Combine(root, "meta.json"), Meta(1023));
            Assert.Equal((ushort)1023, ContentImcEdits.ReadInstalled(root).Edits!.Active(Body, null)!.Mask);
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>Reads the plugin's own source for wiring tests, found by walking up from the test binaries.</summary>
internal static class SourceText
{
    internal static string Read(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }

    /// <summary>The text from <paramref name="signature"/> to the next member at the same indent.</summary>
    internal static string Method(string relative, string signature)
    {
        var src = Read(relative);
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{signature} is not in {relative}");
        int lineStart = src.LastIndexOf('\n', at) + 1;
        var indent = src[lineStart..at];
        int end = src.IndexOf("\n" + indent + "}", at, StringComparison.Ordinal);
        return end < 0 ? src[at..] : src[at..end];
    }
}
