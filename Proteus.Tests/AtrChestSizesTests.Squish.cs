extern alias RefitCli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A physics-variant group beside the builds (NIRÆ's "Squish", #59): its options swap in their own copy of a build's
/// chest model, and must not read as as many builds again. Hand-written manifests in the shape of NIRÆ's 2026-11-02 one.
/// </summary>
public partial class AtrChestSizesTests
{
    private const string Top1801 = "chara/equipment/e0000/model/c1801e0000_top.mdl";

    private static readonly string[] SixLabels =
        ["A · Flat", "A · Small Perky", "A · Large", "B · Flat", "B · Small Perky", "B · Large"];

    private static readonly (string Name, string? File)[] SquishOptions =
    [
        ("Off", null), ("Firm (A)", "top_a_squish"), ("Jelly (A)", "top_a_squish"),
        ("Firm (B)", "top_b_squish"), ("Jelly (B)", "top_b_squish"),
    ];

    /// <summary>NIRÆ's "Squish": chest models only, priority 2; <paramref name="defaultJson"/> is the whole
    /// <c>"DefaultSettings":…,</c> member, or "" for none.</summary>
    private static string SquishGroup(string defaultJson, string type, params (string Name, string? File)[] options)
        => $"{{\"Name\":\"Squish\",\"Type\":\"{type}\",\"Priority\":2,{defaultJson}\"Options\":["
           + string.Join(",", options.Select(o => Option(o.Name,
                 o.File == null ? null : new Dictionary<string, string> { [TopPath] = $"files\\\\{o.File}.mdl" }, null)))
           + "]}";

    private static string Squish(int defaultIndex = 0, string type = "Single")
        => SquishGroup($"\"DefaultSettings\":{defaultIndex},", type, SquishOptions);

    /// <summary>Builds that ship the chest alone, legs elsewhere: rule (b) has no build pairing chest and legs to go by.</summary>
    private static string ChestOnlyBuilds(bool heading = false)
        => Group("Build", "Single", 0, null,
                 (heading ? new[] { Option("--- Builds ---", null, null) } : [])
                 .Concat(new[] { "A", "B" }.SelectMany(b => new[] { "Small", "Large" }.Select(h =>
                     Option($"{b} · {h}", new Dictionary<string, string> { [TopPath] = $"files\\\\top_{b.ToLowerInvariant()}.mdl" },
                            null)))).ToArray());

    private static int ChestEntries(BodySizeCatalog cat) => cat.For("_top").Count(BodySizeCatalog.IsShape);

    // ── AC-1 ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-1")]
    public void Squish_group_adds_no_chest_sizes()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Belly(), Squish()));
        var cat = mod.Catalog;
        var top = cat.For("_top");
        Assert.Equal(SixLabels, top.Select(o => o.Label));
        Assert.All(top, o => Assert.Equal("Chest", o.Group));
        Assert.DoesNotContain(top, o => o.Rel.Contains("squish", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([(@"files\top_a_squish.mdl", @"files\top_a.mdl"), (@"files\top_b_squish.mdl", @"files\top_b.mdl")],
                     cat.Variants.Select(v => (v.Rel, v.BuildRel)).Order());
    }

    // ── AC-2 ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-2")]
    public void Worn_squish_reads_its_build_chest()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Belly(), Squish()));
        var cat = mod.Catalog;
        Assert.Equal("B · Flat",
                     RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_b_squish.mdl"), Pick("Chest", "Flat"))!.Label);
        var resolved = new Dictionary<string, string?> { [TopPath] = Path.Combine(cat.ModRoot, "files", "top_b_squish.mdl") };
        Assert.Equal("B · Flat", AutoRefitTargets.WornFrom(cat, "_top", "0201", resolved, Pick("Chest", "Flat"))!.Label);

        // The pending fill saves the build's chest, never the variant's file.
        var body = new BodyModIndex.Entry("nir", "NIRÆ body", cat);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "nir");
        pref.PendingFill["_top"] = false;
        var targets = new AutoRefitTargets(pref, d => d == "nir" ? body : null, "0201",
            new Dictionary<string, BodyOption?> { ["_top"] = AutoRefitTargets.WornFrom(cat, "_top", "0201", resolved, Pick("Chest", "Flat")) });
        Assert.True(AutoRefitDecisions.ApplySnapshot(pref, targets, new Dictionary<string, string> { ["_top"] = "nir" }, out _));
        Assert.Equal(@"files\top_b.mdl|Flat", pref.Chest!.Rel);
    }

    // ── AC-3 ────────────────────────────────────────────────────────────────────────────────────────────

    private static BodySizeRef SavedSquish(string rel = @"files\top_b_squish.mdl|Small Perky")
        => new() { Rel = rel, Group = "Chest", Section = "Off · Firm (B)", Name = "Small Perky" };

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-3")]
    public void Saved_squish_size_resolves_to_its_build_chest()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Belly(), Squish()));
        var body = new BodyModIndex.Entry("nir", "NIRÆ body", mod.Catalog);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "nir");
        var saved = SavedSquish();
        pref.SetSize("_top", saved);
        var targets = new AutoRefitTargets(pref, d => d == "nir" ? body : null, "0201");
        Assert.Equal("B · Small Perky", targets.Chosen("_top")!.Label);
        var row = Proteus.Gui.AutoRefitPanel.Rows(pref, d => d == "nir" ? body : null, "0201").Single(r => r.Slot == "_top");
        Assert.Equal("B · Small Perky", Proteus.Gui.AutoRefitPanel.SizeLabel(row));
        Assert.Equal(@"files\top_b_squish.mdl|Small Perky", pref.SizeOf("_top")!.Rel);
    }

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-3")]
    public void Saved_bare_squish_rel_takes_the_ticked_chest()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Belly(), Squish()));
        var body = new BodyModIndex.Entry("nir", "NIRÆ body", mod.Catalog);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "nir");
        pref.SetSize("_top", SavedSquish(@"files\top_b_squish.mdl"));
        var targets = new AutoRefitTargets(pref, d => d == "nir" ? body : null, "0201");
        Assert.Equal("B · Flat", targets.With("0201", new Dictionary<string, BodyOption?>(), _ => Pick("Chest", "Flat"))
                                        .Chosen("_top")!.Label);
        Assert.Equal("B · Large", targets.Chosen("_top")!.Label);
    }

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-3")]
    public void Remembered_and_cli_squish_rels_find_the_build()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Belly(), Squish()));
        var cat = mod.Catalog;
        Assert.Equal("A · Flat", BodySizeCatalog.ByFile(cat.For("_top"), cat.Canonical(@"files\top_a_squish.mdl|Flat"), null)!.Label);
        Assert.Equal("A · Small Perky",
                     RefitCli::Proteus.Refit.Program.Find(cat, "_top", "files/top_a_squish.mdl|Small Perky").Label);
    }

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-3")]
    public void Saved_squish_size_on_the_lead_body_resolves()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Belly(), Squish()));
        using var other = new Mod(Meta(null, Builds()));
        var lead = new BodyModIndex.Entry("nir", "NIRÆ body", mod.Catalog);
        var mine = new BodyModIndex.Entry("other", "Other", other.Catalog);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "other");
        pref.SetSize("_top", SavedSquish(@"files\top_b_squish.mdl|Flat"));
        var targets = new AutoRefitTargets(pref, d => d == "nir" ? lead : d == "other" ? mine : null, "0201");
        var to = targets.CoverTarget("_top", lead, Entry(lead.Catalog, "A · Large"), leadStays: true, own: null);
        Assert.NotNull(to);
        Assert.Same(lead, to!.Value.Body);
        Assert.Equal("B · Flat", to.Value.To.Label);
    }

    // ── AC-4, AC-5 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-4")]
    public void Bodies_without_variant_groups_list_as_before()
    {
        using var mod = new Mod(Nirae());
        Assert.Equal(SixLabels, mod.Catalog.For("_top").Select(o => o.Label));
        Assert.Equal(@"files\top_a.mdl|Flat", mod.Catalog.For("_top")[0].Rel);
        Assert.Empty(mod.Catalog.Variants);
        using var plain = new Mod(Meta(null, Builds(), Belly()));
        Assert.Equal(4, plain.Catalog.For("_top").Count);
        Assert.Equal(0, ChestEntries(plain.Catalog));
        Assert.Empty(plain.Catalog.Variants);
    }

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-5")]
    public void Build_number_is_at_least_1419() => Assert.True(Plugin.BuildNumber >= 1419, $"build {Plugin.BuildNumber}");

    // ── the edges ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-1")]
    public void Variant_group_before_builds_still_lists_builds()
    {
        using var mod = new Mod(Meta(null, Squish(), Builds(), Chests()));
        Assert.Equal(SixLabels, mod.Catalog.For("_top").Select(o => o.Label));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-2")]
    public void Lone_build_takes_unnamed_variants()
    {
        using var mod = new Mod(Meta($"\"{TopPath}\":\"files\\\\top.mdl\"", Chests(),
            SquishGroup("\"DefaultSettings\":0,", "Single", ("Off", null), ("Firm", "top_squish"), ("Jelly", "top_squish"))));
        var cat = mod.Catalog;
        Assert.Equal(ChestNames, cat.For("_top").Select(o => o.Label));
        Assert.Equal("Large", RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_squish.mdl"))!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-3")]
    public void Unnameable_variant_is_dropped_and_unknown()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(),
            SquishGroup("\"DefaultSettings\":0,", "Single", ("Off", null), ("Firm", "top_b_squish"), ("Jelly", "top_b_squish"))));
        var cat = mod.Catalog;
        Assert.Equal(SixLabels, cat.For("_top").Select(o => o.Label));
        Assert.Empty(cat.Variants);
        Assert.Null(RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_b_squish.mdl"), Pick("Chest", "Flat")));
        Assert.Equal(@"files\top_b_squish.mdl|Flat", cat.Canonical(@"files\top_b_squish.mdl|Flat"));
        Assert.Null(AutoRefitDecisions.Resolve(cat.For("_top"), cat.Canonical(SavedSquish())));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-4")]
    public void Uncrossed_body_keeps_variant_options()
    {
        using var mod = new Mod(Meta(null, Builds(), Squish()));
        var top = mod.Catalog.For("_top");
        Assert.Equal(4, top.Count(o => o.Group == "Build & Hips"));
        Assert.Equal(4, top.Count(o => o.Group == "Squish"));
        Assert.Equal(0, ChestEntries(mod.Catalog));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-5")]
    public void Variant_sharing_does_not_open_the_gate()
    {
        var sizes = Group("Size", "Single", 0, null,
            Option("S", new Dictionary<string, string> { [TopPath] = "files\\\\top_a.mdl" }, null),
            Option("L", new Dictionary<string, string> { [TopPath] = "files\\\\top_b.mdl" }, null));
        using var mod = new Mod(Meta(null, sizes, Chests(), Squish()));
        Assert.Equal(0, ChestEntries(mod.Catalog));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-6")]
    public void No_build_group_left_falls_back_to_all()
    {
        using var mod = new Mod(Meta(null, ChestOnlyBuilds(heading: true), Chests(), Squish()));
        Assert.Equal(12, ChestEntries(mod.Catalog));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-7")]
    public void Multi_off_group_counts_as_builds()
    {
        using var mod = new Mod(Meta(null, ChestOnlyBuilds(), Chests(), Squish(type: "Multi")));
        Assert.Equal(12, ChestEntries(mod.Catalog));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-8")]
    public void Default_index_missing_clamped_or_unknown()
    {
        using (var none = new Mod(Meta(null, ChestOnlyBuilds(), Chests(), SquishGroup("", "Single", SquishOptions))))
            Assert.Equal(6, ChestEntries(none.Catalog));
        var offLast = SquishOptions.Skip(1).Append(("Off", (string?)null)).ToArray();
        using (var clamped = new Mod(Meta(null, ChestOnlyBuilds(), Chests(), SquishGroup("\"DefaultSettings\":99,", "Single", offLast))))
            Assert.Equal(6, ChestEntries(clamped.Catalog));
        foreach (var bad in new[] { "\"DefaultSettings\":\"x\",", "\"DefaultSettings\":-1," })
        {
            using var odd = new Mod(Meta(null, ChestOnlyBuilds(), Chests(), SquishGroup(bad, "Single", SquishOptions)));
            Assert.Equal(12, ChestEntries(odd.Catalog));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-9")]
    public void Variant_file_ambiguous_across_races_is_dropped()
    {
        // Build A ships top_a for c0201 but top_b for c1801 (and B the other way): "Firm (A)" is a different file's
        // variant at each.
        Dictionary<string, string> BuildFiles(string b, string h) => new()
        {
            [TopPath] = $"files\\\\top_{b}.mdl", [Top1801] = $"files\\\\top_{(b == "a" ? "b" : "a")}.mdl",
            [DwnPath] = $"files\\\\dwn_{b}_{h}.mdl",
        };
        var builds = Group("Build & Hips", "Single", 0, null,
            new[] { "a", "b" }.SelectMany(b => Hips.Select(h => Option($"{b.ToUpperInvariant()} · {h} hips", BuildFiles(b, h), null)))
                              .ToArray());
        var squish = $"{{\"Name\":\"Squish\",\"Type\":\"Single\",\"Priority\":2,\"DefaultSettings\":0,\"Options\":["
                     + Option("Off", null, null) + ","
                     + Option("Firm (A)", new Dictionary<string, string> { [TopPath] = "files\\\\top_a_squish.mdl", [Top1801] = "files\\\\top_a_squish.mdl" }, null)
                     + "]}";
        using var mod = new Mod(Meta(null, builds, Chests(), squish));
        Assert.Empty(mod.Catalog.Variants);
        Assert.DoesNotContain(mod.Catalog.Options, o => o.Rel.Contains("squish", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-10")]
    public void Squish_rel_case_and_slashes()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Squish()));
        Assert.Equal("B · Flat", RefitCli::Proteus.Refit.Program.Find(mod.Catalog, "_top", "FILES/TOP_B_SQUISH.MDL|flat").Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-11")]
    public void Worn_squish_with_unknown_tick_reads_default()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Squish()));
        var cat = mod.Catalog;
        foreach (var ticks in new IReadOnlyDictionary<string, List<string>>?[] { null, new Dictionary<string, List<string>>(), Pick("Chest", "Gone") })
            Assert.Equal("B · Large", RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_b_squish.mdl"), ticks)!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-12")]
    public void Canonical_copies_never_mutates()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Squish()));
        var saved = SavedSquish();
        var mapped = mod.Catalog.Canonical(saved)!;
        Assert.NotSame(saved, mapped);
        Assert.Equal(@"files\top_b.mdl|Small Perky", mapped.Rel);
        Assert.Equal(@"files\top_b_squish.mdl|Small Perky", saved.Rel);
        Assert.Equal((saved.Group, saved.Section, saved.Name), (mapped.Group, mapped.Section, mapped.Name));
        var plain = new BodySizeRef { Rel = @"files\top_a.mdl|Flat" };
        Assert.Same(plain, mod.Catalog.Canonical(plain));
        Assert.Null(mod.Catalog.Canonical((BodySizeRef?)null));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-13")]
    public void Ends_unchanged_with_squish()
    {
        using var mod = new Mod(Meta(null, Builds(hands: true), Chests(), Belly(), Squish()));
        Assert.Equal(4, mod.Catalog.For("_dwn").Count);
        Assert.Equal(4, mod.Catalog.For("_glv").Count);
        Assert.DoesNotContain(mod.Catalog.For("_dwn").Concat(mod.Catalog.For("_glv")), BodySizeCatalog.IsShape);
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-15")]
    public void Chest_bearing_squish_default_is_still_a_variant()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Squish(defaultIndex: 1)));
        Assert.Equal(SixLabels, mod.Catalog.For("_top").Select(o => o.Label));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-16")]
    public void Unpaired_builds_and_chest_default_list_both()
    {
        using var mod = new Mod(Meta(null, ChestOnlyBuilds(), Chests(), Squish(defaultIndex: 1)));
        Assert.Equal(12, ChestEntries(mod.Catalog));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-17")]
    public void Missing_variant_file_is_still_mapped_by_name()
    {
        foreach (bool squishFirst in new[] { false, true })
        {
            using var mod = new Mod(squishFirst ? Meta(null, Squish(), Builds(), Chests()) : Meta(null, Builds(), Chests(), Squish()));
            File.Delete(Path.Combine(mod.Root, "files", "top_b_squish.mdl"));
            var cat = mod.Catalog;
            Assert.Equal(SixLabels, cat.For("_top").Select(o => o.Label));
            Assert.All(cat.For("_top"), o => { Assert.Equal("Chest", o.Group); Assert.True(BodySizeCatalog.IsShape(o)); });
            Assert.Contains(cat.Variants, v => v.Rel == @"files\top_b_squish.mdl" && v.BuildRel == @"files\top_b.mdl");
            Assert.Equal(@"files\top_b.mdl|Flat", cat.Canonical(@"files\top_b_squish.mdl|Flat"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-18")]
    public void Squish_build_leads_unchosen_legs()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Squish()));
        var cat = mod.Catalog;
        var chest = RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_a_squish.mdl"), Pick("Chest", "Flat"))!;
        Assert.Equal("A · Flat", chest.Label);
        var legs = cat.For("_dwn");
        Assert.Equal("A · Small hips",
                     AutoRefitDecisions.Companion(legs, chest, legs.Single(o => o.Label == "B · Large hips"))!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-20")]
    public void Cross_sex_cli_matches_squish_rels()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), Squish()));
        var cat = mod.Catalog;
        Assert.Equal("B · Flat", RefitCli::Proteus.Refit.CrossSexCommand.MadeOn(cat, "_top", 201, "files/top_b_squish.mdl|Flat")!.Label);
        Assert.Null(RefitCli::Proteus.Refit.CrossSexCommand.UnknownTarget(cat, 201, "_top", ["files/top_a_squish.mdl|Large"]));
        Assert.Equal("files/gone.mdl", RefitCli::Proteus.Refit.CrossSexCommand.UnknownTarget(cat, 201, "_top", ["files/gone.mdl"]));
        Assert.Equal("files/top_a.mdl|Flat",
                     RefitCli::Proteus.Refit.CrossSexCommand.DuplicateTarget(cat, ["files/top_a_squish.mdl|Flat", "files/top_a.mdl|Flat"]));
        Assert.Null(RefitCli::Proteus.Refit.CrossSexCommand.DuplicateTarget(cat, ["files/top_a.mdl|Flat", "files/top_b.mdl|Flat"]));
        // Split as the command splits rel=label: the labels do not hide the duplicate.
        var labelled = new[] { "files/top_a_squish.mdl|Flat=X", "files/top_a.mdl|Flat=Y" }.Select(t => t.Split('=', 2)[0]);
        Assert.Equal("files/top_a.mdl|Flat", RefitCli::Proteus.Refit.CrossSexCommand.DuplicateTarget(cat, labelled));
    }

    [Fact]
    [Trait("Plan", "AC-59-nirae-squish-3")]
    public void Saved_squish_size_of_a_part_taken_along_resolves()
    {
        // A legs garment made on NIRÆ and staying there takes the chest along onto NIRÆ, though the chest is set to
        // another body: the size saved for the chest, against a Squish model, is its build's.
        using var mod = new Mod(Meta(null, Builds(), Chests(), Belly(), Squish()));
        using var other = new Mod(Meta(null, Builds()));
        var lead = new BodyModIndex.Entry("nir", "NIRÆ body", mod.Catalog);
        var mine = new BodyModIndex.Entry("other", "Other", other.Catalog);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "other");
        pref.SetSize("_top", SavedSquish(@"files\top_b_squish.mdl|Flat"));
        var targets = new AutoRefitTargets(pref, d => d == "nir" ? lead : d == "other" ? mine : null, "0201");
        var legs = mod.Catalog.For("_dwn").First();
        var plan = targets.PlanParts("_dwn", lead, legs, "nir", legs, [("_top", Entry(lead.Catalog, "A · Large"))]);
        var top = Assert.Single(plan.Others, p => p.Slot == "_top");
        Assert.Same(lead, top.Body);
        Assert.Equal("B · Flat", top.To.Label);
    }

    private static string SquishNamed(params (string Name, string File)[] options)
        => SquishGroup("\"DefaultSettings\":0,", "Single", [("Off", null), .. options.Select(o => (o.Name, (string?)o.File))]);

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-3")]
    public void Variant_naming_several_builds_is_unmapped()
    {
        using var mod = new Mod(Meta(null, Builds(), Chests(), SquishNamed(("Firm (A B)", "top_b_squish"), ("Jelly (A B)", "top_b_squish"))));
        Assert.Empty(mod.Catalog.Variants);
        Assert.Equal(SixLabels, mod.Catalog.For("_top").Select(o => o.Label));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-3")]
    public void Variant_whose_options_name_different_builds_is_unmapped()
    {
        // One option names A, the other no build at all: every option must name it.
        using var mod = new Mod(Meta(null, Builds(), Chests(), SquishNamed(("Firm (A)", "top_b_squish"), ("Jelly", "top_b_squish"))));
        Assert.Empty(mod.Catalog.Variants);
        Assert.Equal(SixLabels, mod.Catalog.For("_top").Select(o => o.Label));
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-3")]
    public void Variant_of_a_builds_own_file_is_that_build()
    {
        // Named for B, but the very file of A: it is A's, and a size saved on A stays A's.
        using var mod = new Mod(Meta(null, Builds(), Chests(), SquishNamed(("Firm (B)", "top_a"), ("Jelly (B)", "top_a"))));
        var cat = mod.Catalog;
        Assert.Equal(SixLabels, cat.For("_top").Select(o => o.Label));
        var own = Assert.Single(cat.Variants);
        Assert.Equal(RefitCore.Normal(own.Rel), RefitCore.Normal(own.BuildRel));
        Assert.Equal(@"files\top_a.mdl|Flat", cat.Canonical(@"files\top_a.mdl|Flat"));
        Assert.Equal("A · Flat", RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_a.mdl"), Pick("Chest", "Flat"))!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-59-nirae-squish-9")]
    public void Build_file_reused_as_another_races_variant_is_dropped()
    {
        // c0201's B model, swapped in at c1801 by a variant named for A — whose c1801 model is another file. Read by file,
        // it would turn every size saved on c0201's B into A's.
        Dictionary<string, string> BuildFiles(string b, string h) => new()
        {
            [TopPath] = $"files\\\\top_{b}.mdl", [Top1801] = $"files\\\\top_{(b == "a" ? "c" : "d")}.mdl",
            [DwnPath] = $"files\\\\dwn_{b}_{h}.mdl",
        };
        var builds = Group("Build & Hips", "Single", 0, null,
            new[] { "a", "b" }.SelectMany(b => Hips.Select(h => Option($"{b.ToUpperInvariant()} · {h} hips", BuildFiles(b, h), null)))
                              .ToArray());
        var squish = $"{{\"Name\":\"Squish\",\"Type\":\"Single\",\"Priority\":2,\"DefaultSettings\":0,\"Options\":["
                     + Option("Off", null, null) + ","
                     + Option("Firm (A)", new Dictionary<string, string> { [Top1801] = "files\\\\top_b.mdl" }, null) + "]}";
        using var mod = new Mod(Meta(null, builds, Chests(), squish));
        Assert.Empty(mod.Catalog.Variants);
        Assert.Equal(@"files\top_b.mdl|Flat", mod.Catalog.Canonical(@"files\top_b.mdl|Flat"));
    }
}
