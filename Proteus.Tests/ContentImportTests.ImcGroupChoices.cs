using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SyntheticPack;

namespace Proteus.Tests;

/// <summary>
/// An installed mod's choices on its Penumbra <c>Imc</c> groups carried into the import, and every other import's
/// <c>Imc</c> groups written at the pack's defaults so a reused folder name's old ticks never come back.
/// </summary>
public partial class ContentImportTests
{
    private const string DwnModel = "chara/equipment/e6058/model/c0101e6058_dwn.mdl";
    private static readonly string[] DwnAttrs = ["atr_dv_a", "atr_dv_b"];

    /// <summary>The preview of <paramref name="pack"/> as an installed folder under <paramref name="dir"/>.</summary>
    private static ContentImportService.ImportPreview InstalledPreview(SyntheticPack pack, string dir)
        => ContentImportService.Inspect(Installed(pack.Path, dir));

    /// <summary>Runs <paramref name="test"/> with a fresh temp dir, deleted after.</summary>
    private static void InDir(Action<string> test)
    {
        var dir = TempDir();
        try { test(dir); }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[AC-imc-group-choices-1] The original's ticks replace the defaults, in pack order.</summary>
    [Fact]
    public void An_installed_import_copies_the_sources_imc_group_choices() => InDir(dir =>
    {
        using var pack = ImcGroupPack(imcDefaultSettings: 0b01);
        var preview = InstalledPreview(pack, dir);

        Assert.Equal(["atr_dv_b Hide"],
            ContentImportService.ImportSelection(preview, Source(("Toggles", ["atr_dv_b Hide"])))["Toggles"]);
        Assert.Equal(["atr_dv_a Hide", "atr_dv_b Hide"],
            ContentImportService.ImportSelection(preview, Source(("Toggles", ["atr_dv_b Hide", "atr_dv_a Hide"])))["Toggles"]);
    });

    /// <summary>[AC-imc-group-choices-1] [EDGE-imc-group-choices-2] Nothing ticked on the original is copied as nothing.</summary>
    [Fact]
    public void An_imc_group_with_nothing_ticked_is_copied_as_empty() => InDir(dir =>
    {
        using var pack = ImcGroupPack(imcDefaultSettings: 0b11);
        Assert.Empty(ContentImportService.ImportSelection(InstalledPreview(pack, dir), Source(("Toggles", [])))["Toggles"]);
    });

    /// <summary>[AC-imc-group-choices-1] [EDGE-imc-group-choices-5] A ticked Disable is copied with the rest.</summary>
    [Fact]
    public void A_ticked_disable_option_is_copied() => InDir(dir =>
    {
        using var pack = ImcGroupPack(disableOption: true);
        var preview = InstalledPreview(pack, dir);
        Assert.Equal(["Disable"], ContentImportService.ImportSelection(preview, Source(("Toggles", ["Disable"])))["Toggles"]);
        Assert.Equal(["atr_dv_a Hide", "Disable"],
            ContentImportService.ImportSelection(preview, Source(("Toggles", ["Disable", "atr_dv_a Hide"])))["Toggles"]);
    });

    /// <summary>
    /// [AC-imc-group-choices-2] The copied choice hides, through the composite's own reading, exactly the parts the
    /// pack's masks give it: the group's default entry with each ticked option XORed in. With no ticks the source
    /// names no choice at all, so the pack's own defaults are what is written and checked.
    /// </summary>
    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(0ul, "atr_dv_a Hide")]
    [InlineData(0ul, "atr_dv_b Hide")]
    [InlineData(1ul, "atr_dv_a Hide", "atr_dv_b Hide")]
    public void A_copied_imc_group_choice_hides_the_parts_the_packs_masks_give(ulong defaults, params string[] ticked)
        => InDir(dir =>
    {
        using var pack = ImcGroupPack(imcDefaultSettings: defaults);
        var folder = Installed(pack.Path, dir);
        var preview = ContentImportService.Inspect(folder);
        var selection = ContentImportService.ImportSelection(preview,
            ticked.Length == 0 ? null : Source(("Toggles", ticked)));

        var g = PenumbraPackage.Read(folder).Groups.Single(x => x.Type == "Imc");
        int mask = g.DefaultAttributeMask;
        foreach (var o in g.Options.Where(o => selection["Toggles"].Contains(o.Name))) mask ^= o.AttributeMask;
        var expected = DwnAttrs.Where(a => (mask & (1 << (a[^1] - 'a'))) == 0).Order();

        var actual = ContentPieceResolver.HiddenAttributes(
            ContentImportService.BuildSidecar(preview, "x", "y").ContentAttributes, DwnModel, DwnAttrs, selection);
        Assert.Equal(expected, (actual ?? new HashSet<string>()).Order());
    });

    /// <summary>[AC-imc-group-choices-2] The copied choice is in place before the import's one recomposite, which reads it live.</summary>
    [Fact]
    public void Wiring_finish_applies_the_selection_before_its_recomposite()
    {
        var finish = SourceText.Method("Proteus/Services/ContentImportService.cs", "private ImportResult Finish(");
        int apply = finish.IndexOf("ApplyImportSelection(collId.Value, dirName, preview, sourceSelection)", StringComparison.Ordinal);
        int recomposite = finish.IndexOf("TriggerRecomposite(\"content-imported\")", StringComparison.Ordinal);
        Assert.True(apply >= 0 && recomposite > apply);

        var resolution = SourceText.Read("Proteus/Services/SecondSkinService.ContentResolution.cs");
        int at = resolution.IndexOf("ContentImcVisibility.For(", StringComparison.Ordinal);
        Assert.Contains("ModSelection(cEntry.ModDirectory)", resolution[at..(at + 600)]);
    }

    /// <summary>[AC-imc-group-choices-3] A group for an item nobody imported is copied too, and the sidecar gains nothing.</summary>
    [Fact]
    public void An_imc_group_for_an_item_not_imported_is_copied_too() => InDir(dir =>
    {
        using var pack = ImcGroupPack(otherItemGroup: "Vanilla top");
        var preview = InstalledPreview(pack, dir);
        Assert.Equal(["Hide A"],
            ContentImportService.ImportSelection(preview, Source(("Vanilla top", ["Hide A"])))["Vanilla top"]);

        var fromFolder = ContentImportService.BuildSidecar(preview, "x", "y").ContentAttributes!;
        var fromPmp = ContentImportService.BuildSidecar(ContentImportService.Inspect(pack.Path), "x", "y").ContentAttributes!;
        Assert.Equal(fromPmp.Select(a => (a.Group, a.SetId, a.Slot, a.DefaultMask)),
                     fromFolder.Select(a => (a.Group, a.SetId, a.Slot, a.DefaultMask)));
    });

    /// <summary>[AC-imc-group-choices-4] [EDGE-imc-group-choices-9] Without a usable source the pack's defaults are written; the manifest keeps them.</summary>
    [Fact]
    public void Imc_groups_start_at_the_pack_defaults_without_a_usable_source() => InDir(dir =>
    {
        using var pack = ImcGroupPack(imcDefaultSettings: 0b10);
        var pmp = ContentImportService.Inspect(pack.Path);
        var folder = InstalledPreview(pack, dir);

        Assert.Equal(["atr_dv_b Hide"], ContentImportService.ImportSelection(pmp)["Toggles"]);
        Assert.Equal(["atr_dv_b Hide"], ContentImportService.ImportSelection(folder)["Toggles"]);
        Assert.Empty(ContentImportService.UncopiedImcChoices(folder, null));

        var root = Path.Combine(dir, "out");
        ContentImportService.WriteMod(root, "Toggled", "Tests", pmp);
        Assert.Equal(2, (int)WrittenGroups(root).Single(g => (string?)g!["Type"] == "Imc")!["DefaultSettings"]!);
    });

    /// <summary>[AC-imc-group-choices-4] [EDGE-imc-group-choices-10] A Proteus mod is installed as its author made it.</summary>
    [Fact]
    public void An_install_only_source_copies_nothing() => InDir(dir =>
    {
        using var pack = ImcGroupPack();
        var preview = InstalledPreview(pack, dir) with { AuthoredSidecar = new ProteusMetadata() };
        var source = Source(("Toggles", ["atr_dv_a Hide"]));
        Assert.Empty(ContentImportService.ImportSelection(preview, source));
        Assert.Empty(ContentImportService.UncopiedImcChoices(preview, source));
    });

    /// <summary>[AC-imc-group-choices-5] Every other group selects exactly as before.</summary>
    [Theory]
    [InlineData("Size", "Single")]
    [InlineData("Extras", "Multi")]
    public void Other_groups_select_as_before_when_imc_groups_are_copied(string name, string type) => InDir(dir =>
    {
        using var pack = ImcGroupPack(leadingGroup: (name, type));
        var preview = InstalledPreview(pack, dir);
        var copied = ContentImportService.ImportSelection(preview, Source(("Toggles", ["atr_dv_a Hide"])));
        var plain = ContentImportService.ImportSelection(preview);

        Assert.Equal(plain.Keys.Order(), copied.Keys.Order());
        foreach (var key in plain.Keys.Where(k => k != "Toggles")) Assert.Equal(plain[key], copied[key]);
    });

    /// <summary>[EDGE-imc-group-choices-1] A group the original's settings do not mention gets the pack's defaults, unreported.</summary>
    [Fact]
    public void An_imc_group_the_source_does_not_mention_gets_its_defaults() => InDir(dir =>
    {
        using var pack = ImcGroupPack(imcDefaultSettings: 0b01, otherItemGroup: "Vanilla top");
        var preview = InstalledPreview(pack, dir);

        var unrelated = Source(("Unrelated", ["x"]));
        Assert.Equal(["atr_dv_a Hide"], ContentImportService.ImportSelection(preview, unrelated)["Toggles"]);
        Assert.Empty(ContentImportService.UncopiedImcChoices(preview, unrelated));

        var onlyToggles = Source(("Toggles", ["atr_dv_b Hide"]));
        Assert.Empty(ContentImportService.ImportSelection(preview, onlyToggles)["Vanilla top"]);
        Assert.Empty(ContentImportService.UncopiedImcChoices(preview, onlyToggles));
    });

    /// <summary>[EDGE-imc-group-choices-3] Case on the original's side does not matter; the pack's exact names are written.</summary>
    [Fact]
    public void Imc_choices_are_written_under_the_packs_exact_group_name() => InDir(dir =>
    {
        using var pack = ImcGroupPack();
        var selection = ContentImportService.ImportSelection(InstalledPreview(pack, dir),
            Source(("toggles", ["ATR_DV_B HIDE"])));

        Assert.Contains("Toggles", selection.Keys, StringComparer.Ordinal);
        Assert.Single(selection.Keys, k => string.Equals(k, "Toggles", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["atr_dv_b Hide"], selection["Toggles"]);
    });

    /// <summary>[EDGE-imc-group-choices-4] [EDGE-imc-group-choices-13] One unknown name gives the defaults, and the log says which.</summary>
    [Theory]
    [InlineData("Nope")]
    [InlineData("atr_dv_a Hide", "Nope")]
    public void An_unknown_option_name_gives_the_defaults_and_is_reported(params string[] ticked) => InDir(dir =>
    {
        using var pack = ImcGroupPack(imcDefaultSettings: 0b10);
        var preview = InstalledPreview(pack, dir);
        var source = Source(("Toggles", ticked));

        Assert.Equal(["atr_dv_b Hide"], ContentImportService.ImportSelection(preview, source)["Toggles"]);
        var group = preview.Pack.Groups.Single(g => g.Type == "Imc");
        Assert.Null(ContentImportService.ImcGroupChoice(group, ticked, out var unknown));
        Assert.Equal("Nope", unknown);

        var (name, reason) = Assert.Single(ContentImportService.UncopiedImcChoices(preview, source));
        Assert.Equal("Toggles", name);
        Assert.Contains("Nope", reason);
    });

    /// <summary>[EDGE-imc-group-choices-6] An option the original lists twice is written once.</summary>
    [Fact]
    public void A_source_option_listed_twice_is_written_once() => InDir(dir =>
    {
        using var pack = ImcGroupPack();
        Assert.Equal(["atr_dv_a Hide"], ContentImportService.ImportSelection(InstalledPreview(pack, dir),
            Source(("Toggles", ["atr_dv_a Hide", "ATR_DV_A HIDE"])))["Toggles"]);
    });

    /// <summary>
    /// [EDGE-imc-group-choices-7] An Imc group an earlier group's name shadows is never written — Penumbra would apply
    /// it to that one — and the log says so; a later Imc duplicate is the one left out.
    /// </summary>
    [Theory]
    [InlineData("Single")]
    [InlineData("Multi")]
    [InlineData("Combining")]
    [InlineData("Imc")]
    public void An_imc_group_shadowed_by_an_earlier_name_is_never_written(string type) => InDir(dir =>
    {
        using var pack = ImcGroupPack(leadingGroup: ("TOGGLES", type));
        var preview = InstalledPreview(pack, dir);
        var source = Source(("Toggles", ["atr_dv_a Hide"]));
        var selection = ContentImportService.ImportSelection(preview, source);

        Assert.DoesNotContain("Toggles", selection.Keys, StringComparer.Ordinal);
        Assert.Contains(ContentImportService.UncopiedImcChoices(preview, source),
            u => u.Group == "Toggles" && u.Reason.Contains("earlier group"));
        if (type == "Multi")
            Assert.Equal(ContentImportService.ImportSelection(preview)["TOGGLES"], selection["TOGGLES"]);
    });

    /// <summary>[EDGE-imc-group-choices-7] A later Imc group of the same name is the one never written.</summary>
    [Fact]
    public void A_later_imc_duplicate_is_never_written() => InDir(dir =>
    {
        using var pack = ImcGroupPack(otherItemGroup: "TOGGLES");
        var selection = ContentImportService.ImportSelection(InstalledPreview(pack, dir),
            Source(("Toggles", ["atr_dv_a Hide"])));
        Assert.Equal(["atr_dv_a Hide"], selection["Toggles"]);
        Assert.DoesNotContain("TOGGLES", selection.Keys, StringComparer.Ordinal);
    });

    /// <summary>
    /// [EDGE-imc-group-choices-7] A later group of the same name is reported as overwriting the copied choice only when
    /// the import really writes it: a Multi always is; this Single, whose one option does nothing, never is.
    /// </summary>
    [Theory]
    [InlineData("Multi", true)]
    [InlineData("Single", false)]
    public void A_later_group_overwriting_the_choice_is_reported_only_when_written(string type, bool written)
        => InDir(dir =>
    {
        using var pack = ImcGroupPack(trailingGroup: ("toggles", type));
        var preview = InstalledPreview(pack, dir);
        var source = Source(("Toggles", ["atr_dv_a Hide"]));

        var selection = ContentImportService.ImportSelection(preview, source);
        Assert.Equal(written, selection.ContainsKey("toggles"));
        Assert.Equal(written, ContentImportService.UncopiedImcChoices(preview, source)
            .Any(u => u.Group == "Toggles" && u.Reason.Contains("later group")));
    });

    /// <summary>[EDGE-imc-group-choices-7] The piece group never takes an Imc group's name.</summary>
    [Fact]
    public void The_piece_group_never_takes_an_imc_groups_name() => InDir(dir =>
    {
        using var pack = ImcGroupPack(groupName: ContentImportService.PieceGroup);
        var preview = InstalledPreview(pack, dir);
        var selection = ContentImportService.ImportSelection(preview,
            Source((ContentImportService.PieceGroup, ["atr_dv_a Hide"])));

        Assert.Equal(["atr_dv_a Hide"], selection[ContentImportService.PieceGroup]);
        var piece = Assert.IsType<string>(preview.PieceGroupName);
        Assert.DoesNotContain(preview.Pack.Groups, g => string.Equals(g.Name, piece, StringComparison.OrdinalIgnoreCase));
    });

    /// <summary>[EDGE-imc-group-choices-8] A v3 installed folder's Imc group is copied the same as a v4 one's.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_v3_installed_folders_imc_group_is_copied_the_same(bool legacy) => InDir(dir =>
    {
        using var pack = ImcGroupPack(legacy: legacy);
        Assert.Equal(["atr_dv_b Hide"], ContentImportService.ImportSelection(InstalledPreview(pack, dir),
            Source(("Toggles", ["atr_dv_b Hide"])))["Toggles"]);
    });

    /// <summary>[EDGE-imc-group-choices-11] A .pmp import writes every Imc group, so ticks inherited by folder name are overwritten.</summary>
    [Fact]
    public void A_pmp_import_writes_imc_defaults_over_inherited_ticks()
    {
        using (var none = ImcGroupPack(imcDefaultSettings: 0))
            Assert.Empty(ContentImportService.ImportSelection(ContentImportService.Inspect(none.Path))["Toggles"]);
        using var one = ImcGroupPack(imcDefaultSettings: 0b01);
        Assert.Equal(["atr_dv_a Hide"], ContentImportService.ImportSelection(ContentImportService.Inspect(one.Path))["Toggles"]);
    }

    /// <summary>[EDGE-imc-group-choices-12] Default bits past the group's options are ignored.</summary>
    [Fact]
    public void Default_bits_past_the_options_are_ignored()
    {
        using var pack = ImcGroupPack(imcDefaultSettings: (1ul << 40) | (1ul << 2) | 1);
        Assert.Equal(["atr_dv_a Hide"], ContentImportService.ImportSelection(ContentImportService.Inspect(pack.Path))["Toggles"]);
    }

    /// <summary>[EDGE-imc-group-choices-13] The import logs each Imc choice it did not copy.</summary>
    [Fact]
    public void Wiring_apply_import_selection_logs_uncopied_imc_choices()
    {
        var apply = SourceText.Method("Proteus/Services/ContentImportService.cs", "private void ApplyImportSelection(");
        Assert.Contains("UncopiedImcChoices(preview, sourceSelection)", apply);
        Assert.Contains("log.Information(", apply);
    }

    /// <summary>[EDGE-imc-group-choices-13] A copied group is not reported.</summary>
    [Fact]
    public void A_copied_group_is_not_reported() => InDir(dir =>
    {
        using var pack = ImcGroupPack();
        Assert.Empty(ContentImportService.UncopiedImcChoices(InstalledPreview(pack, dir),
            Source(("Toggles", ["atr_dv_a Hide"]))));
    });
}
