extern alias RefitCli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A body that ships every chest in one model and picks the one drawn with an option group of <c>Atr</c> switches alone
/// (NIRÆ's "Chest"): its chest sizes are build × chest, each its own size, the chest riding in the size's rel
/// (<see cref="BodySizeCatalog.IsShape"/>). Hand-written manifests in the shape of NIRÆ's.
/// </summary>
public class AtrChestSizesTests
{
    // ── manifests, written by hand ──────────────────────────────────────────────────────────────────────

    private const string TopPath = "chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string DwnPath = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string GlvPath = "chara/equipment/e0000/model/c0201e0000_glv.mdl";

    private static string Atr(string attribute, bool on, string? slot = "Body", string? extra = null)
        => "{\"Type\":\"Atr\",\"Manipulation\":{" + (slot != null ? $"\"Slot\":\"{slot}\"," : "")
           + $"\"Attribute\":\"{attribute}\",\"Entry\":{(on ? "true" : "false")}" + (extra != null ? "," + extra : "") + "}}";

    private static string Option(string name, Dictionary<string, string>? files, string? more, params string[] manips)
        => $"{{\"Name\":\"{name}\",\"Files\":{{{string.Join(",", (files ?? []).Select(f => $"\"{f.Key}\":\"{f.Value}\""))}}},"
           + (more != null ? more + "," : "") + $"\"Manipulations\":[{string.Join(",", manips)}]}}";

    private static string Group(string name, string type, int defaultSettings, string? more, params string[] options)
        => $"{{\"Name\":\"{name}\",\"Type\":\"{type}\",\"Priority\":0,\"DefaultSettings\":{defaultSettings},"
           + (more != null ? more + "," : "") + $"\"Options\":[{string.Join(",", options)}]}}";

    private static string Meta(string? defaultFiles, params string[] groups)
        => "{\"FileVersion\":4,\"Name\":\"Body\",\"Author\":\"x\",\"DefaultData\":{\"Files\":{" + (defaultFiles ?? "")
           + "},\"Manipulations\":[]},\"Groups\":[" + string.Join(",", groups) + "]}";

    private static readonly string[] Hips = ["s", "l"];

    /// <summary>NIRÆ's "Build &amp; Hips": builds A and B, small and large hips, each its own legs and its build's chest.</summary>
    private static string Builds(bool hands = false)
        => Group("Build & Hips", "Single", 0, null,
                 new[] { "A", "B" }.SelectMany(b => Hips.Select(h => Option($"{b} · {(h == "s" ? "Small" : "Large")} hips",
                     Files(b, h, hands),
                     null, Atr("atrx_h_s", h == "s"), Atr("atrx_h_l", h == "l"),
                     Atr("atrx_h_s", h == "s", "Legs"), Atr("atrx_h_l", h == "l", "Legs")))).ToArray());

    private static Dictionary<string, string> Files(string b, string h, bool hands)
    {
        var files = new Dictionary<string, string>
        {
            [TopPath] = $"files\\\\top_{b.ToLowerInvariant()}.mdl",
            [DwnPath] = $"files\\\\dwn_{b.ToLowerInvariant()}_{h}.mdl",
        };
        if (hands) files[GlvPath] = "files\\\\glv.mdl";
        return files;
    }

    private static readonly string[] ChestNames = ["Flat", "Small Perky", "Large"];
    private static readonly string[] ChestAttrs = ["atrx_c_flat", "atrx_c_perky", "atrx_c_large"];

    /// <summary>NIRÆ's "Chest": no files, each option drawing its chest and switching the others off, on the body and
    /// the hands; Large by default.</summary>
    private static string Chests(string type = "Single", string? groupMore = null, string? optionMore = null,
                                 string? slot = "Body", string? extra = null, Dictionary<string, string>? files = null,
                                 int count = 3, string name = "Chest")
        => Group(name, type, 2, groupMore,
                 ChestNames.Take(count).Select((n, i) => Option(n, files, optionMore,
                     ChestAttrs.SelectMany((c, j) => new[] { Atr(c, i == j, slot, extra), Atr(c, i == j, "Hands", extra) })
                               .ToArray())).ToArray());

    private static string Belly() => Group("Belly", "Single", 0, null,
        Option("None", null, null, Atr("atrx_b_none", true), Atr("atrx_b_soft", false), Atr("atrx_b_none", true, "Legs")),
        Option("Soft", null, null, Atr("atrx_b_none", false), Atr("atrx_b_soft", true), Atr("atrx_b_soft", true, "Legs")));

    private static string Nirae() => Meta(null, Builds(), Chests(), Belly());

    private sealed class Mod : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "proteus-chests-" + Guid.NewGuid().ToString("N"));

        public Mod(string meta)
        {
            Directory.CreateDirectory(Path.Combine(Root, "files"));
            File.WriteAllText(Path.Combine(Root, "meta.json"), meta);
            // The chest models carry the chests the Chest group switches between, as NIRÆ's do.
            byte[] chest = SyntheticModel.Build(ChestAttrs, new SyntheticModel.Mesh("mt_c0201b0001_a.mtrl",
                ChestAttrs.Select((_, i) => new SyntheticModel.Sub(1u << i)).ToArray()));
            foreach (string f in new[] { "top_a", "top_b", "top" })
                File.WriteAllBytes(Path.Combine(Root, "files", f + ".mdl"), chest);
            foreach (string f in new[] { "dwn_a_s", "dwn_a_l", "dwn_b_s", "dwn_b_l", "glv" })
                File.WriteAllBytes(Path.Combine(Root, "files", f + ".mdl"), [0]);
        }

        public BodySizeCatalog Catalog => BodySizeCatalog.Read(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    private static Dictionary<string, List<string>> Pick(string group, params string[] options)
        => new() { [group] = [.. options] };

    private static BodyOption Entry(BodySizeCatalog cat, string label) => cat.For("_top").Single(o => o.Label == label);

    private static Func<string, string?> Wears(BodySizeCatalog cat, string file)
        => path => path == TopPath ? Path.Combine(cat.ModRoot, "files", file) : null;

    // ── the list ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-1")]
    public void Chest_lists_every_build_and_chest()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        Assert.Equal(["A · Flat", "A · Small Perky", "A · Large", "B · Flat", "B · Small Perky", "B · Large"],
                     top.Select(o => o.Label));
        Assert.All(top, o => Assert.Equal("Chest", o.Group));
        Assert.Equal("Chest / A · Flat", top[0].FullLabel);
        Assert.Equal(@"files\top_a.mdl|Flat", top[0].Rel);
        Assert.Equal(6, top.Select(o => RefitCore.Normal(o.Rel)).Distinct().Count());
        Assert.Equal(["A · Large", "B · Large"], top.Where(o => o.DefaultShape).Select(o => o.Label));
        Assert.All(top, o => Assert.True(BodySizeCatalog.IsShape(o)));
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-2")]
    public void Settings_rows_offer_the_chest_sizes()
    {
        using var mod = new Mod(Nirae());
        var body = new BodyModIndex.Entry("nir", "NIRÆ body", mod.Catalog);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "nir");
        pref.SetSize("_top", AutoRefitDecisions.RefOf(Entry(body.Catalog, "B · Small Perky")));
        var row = Proteus.Gui.AutoRefitPanel.Rows(pref, d => d == "nir" ? body : null, "0201").Single(r => r.Slot == "_top");
        Assert.Equal(6, row.Options.Count);
        Assert.Equal("B · Small Perky", Proteus.Gui.AutoRefitPanel.SizeLabel(row));
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-2")]
    public void A_picked_chest_survives_save_and_load()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        var kept = AutoRefitDecisions.RefOf(Entry(mod.Catalog, "B · Small Perky"));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(kept);
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<BodySizeRef>(json);
        Assert.Equal(Entry(mod.Catalog, "B · Small Perky"), AutoRefitDecisions.Resolve(top, back));
        Assert.Equal(Entry(BodySizeCatalog.Read(mod.Root), "B · Small Perky"), AutoRefitDecisions.Resolve(top, back));
    }

    // ── the refit ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-3")]
    public void Refit_onto_a_chest_entry_draws_that_chest_only()
    {
        using var mod = new Mod(Nirae());
        var cat = mod.Catalog;
        var entry = Entry(cat, "B · Small Perky");
        var draw = RefitCore.MaskOf(mod.Root, cat, "_top", Pick("Chest", "Flat"), 201)!.Onto(entry);
        Assert.Equal(["atrx_c_flat", "atrx_c_large"], draw.AtrOff.Where(a => a.StartsWith("atrx_c_")).Order(StringComparer.Ordinal));
        Assert.EndsWith(Path.Combine("files", "top_b.mdl"), cat.PathOf(entry));
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-3")]
    public void The_cli_fits_the_chest_entry_it_is_given()
    {
        using var mod = new Mod(Nirae());
        var cat = mod.Catalog;
        var entry = RefitCli::Proteus.Refit.Program.Find(cat, "_top", "files/top_b.mdl|Small Perky");
        Assert.Equal("B · Small Perky", entry.Label);
        var draw = RefitCli::Proteus.Refit.Program.MaskOf(cat, "_top", "0201")!.Onto(entry);
        Assert.Equal(["atrx_c_flat", "atrx_c_large"], draw.AtrOff.Where(a => a.StartsWith("atrx_c_")).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-4")]
    public void Worn_chest_is_read_from_file_and_ticked_chest()
    {
        using var mod = new Mod(Nirae());
        var cat = mod.Catalog;
        Assert.Equal("B · Large",
                     RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_b.mdl"), Pick("Chest", "Large"))!.Label);
        Assert.Equal("B · Flat",
                     RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_b.mdl"), Pick("Chest", "Flat"))!.Label);
        var resolved = new Dictionary<string, string?> { [TopPath] = Path.Combine(cat.ModRoot, "files", "top_b.mdl") };
        Assert.Equal("B · Flat", AutoRefitTargets.WornFrom(cat, "_top", "0201", resolved, Pick("Chest", "Flat"))!.Label);

        // The pending fill saves the chest worn.
        var body = new BodyModIndex.Entry("nir", "NIRÆ body", cat);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "nir");
        pref.PendingFill["_top"] = false;
        var worn = AutoRefitTargets.WornFrom(cat, "_top", "0201", resolved, Pick("Chest", "Small Perky"));
        var targets = new AutoRefitTargets(pref, d => d == "nir" ? body : null, "0201",
                                           new Dictionary<string, BodyOption?> { ["_top"] = worn });
        Assert.True(AutoRefitDecisions.ApplySnapshot(pref, targets, new Dictionary<string, string> { ["_top"] = "nir" }, out _));
        Assert.Equal(@"files\top_b.mdl|Small Perky", pref.Chest!.Rel);
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-5")]
    public void Two_chests_of_one_model_are_two_sizes()
    {
        using var mod = new Mod(Nirae());
        var flat = Entry(mod.Catalog, "A · Flat");
        var large = Entry(mod.Catalog, "A · Large");
        Assert.False(AutoRefitDecisions.SameFile("nir", flat, "nir", large));
        Assert.True(AutoRefitDecisions.SameFile("nir", flat, "nir", flat));
        Assert.NotEqual(AutoRefitWatcher.TargetsOf("_top", flat, [])["_top"], AutoRefitWatcher.TargetsOf("_top", large, [])["_top"]);
        Assert.Equal("NIRÆ body — A · Flat", RefitCore.OptionName("NIRÆ body", [flat.Label]));
        Assert.NotEqual(RefitCore.OptionName("NIRÆ body", [flat.Label]), RefitCore.OptionName("NIRÆ body", [large.Label]));
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-6")]
    public void An_old_saved_chest_resolves_to_its_build()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        var old = new BodySizeRef { Rel = @"files\top_b.mdl", Group = "Build & Hips", Section = "", Name = "B · Large hips" };
        Assert.Equal("B · Large", AutoRefitDecisions.Resolve(top, old)!.Label);
        Assert.Equal("B · Flat", AutoRefitDecisions.Resolve(top, old, Entry(mod.Catalog, "B · Flat"))!.Label);
        Assert.Equal("B · Large", AutoRefitDecisions.Resolve(top, old, Entry(mod.Catalog, "A · Flat"))!.Label);
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-7")]
    public void Legs_keep_their_build_and_hips_list()
    {
        using var mod = new Mod(Nirae());
        var legs = mod.Catalog.For("_dwn");
        Assert.Equal(["A · Small hips", "A · Large hips", "B · Small hips", "B · Large hips"], legs.Select(o => o.Label));
        Assert.All(legs, o => Assert.False(BodySizeCatalog.IsShape(o)));
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-7")]
    public void A_body_without_a_chest_group_lists_as_before()
    {
        using var mod = new Mod(Meta(null, Builds(), Belly()));
        var top = mod.Catalog.For("_top");
        Assert.Equal([
            new BodyOption("Build & Hips", "", "A · Small hips", @"files\top_a.mdl", TopPath, "_top"),
            new BodyOption("Build & Hips", "", "A · Large hips", @"files\top_a.mdl", TopPath, "_top"),
            new BodyOption("Build & Hips", "", "B · Small hips", @"files\top_b.mdl", TopPath, "_top"),
            new BodyOption("Build & Hips", "", "B · Large hips", @"files\top_b.mdl", TopPath, "_top"),
        ], top);
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-8")]
    public void Cli_lists_and_finds_chest_entries()
    {
        using var mod = new Mod(Nirae());
        var cat = mod.Catalog;
        Assert.Equal("B · Large", RefitCli::Proteus.Refit.Program.Find(cat, "_top", "files/top_b.mdl").Label);
        Assert.Equal("A · Flat", RefitCli::Proteus.Refit.Program.Find(cat, "_top", "FILES/TOP_A.MDL|flat").Label);
        Assert.Contains("quote it in a shell", RefitCli::Proteus.Refit.Program.Usage);
    }

    [Fact]
    [Trait("Plan", "AC-atr-chest-sizes-9")]
    public void Build_number_is_1405() => Assert.Equal(1405, Plugin.BuildNumber);

    // ── edges ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-1")]
    public void Shared_chest_model_without_atr_group_is_unchanged()
    {
        using var mod = new Mod(Meta(null, Group("Size", "Single", 0, null,
            Option("S", new() { [TopPath] = @"files\\top_a.mdl" }, null),
            Option("M", new() { [TopPath] = @"files\\top_a.mdl" }, null),
            Option("L", new() { [TopPath] = @"files\\top_a.mdl" }, null))));
        Assert.Equal(["S", "M", "L"], mod.Catalog.For("_top").Select(o => o.Label));
        Assert.All(mod.Catalog.For("_top"), o => Assert.Equal(@"files\top_a.mdl", o.Rel));
    }

    public static TheoryData<string> Unqualified => new()
    {
        Chests(type: "Multi"),
        Chests(type: "Combining"),
        Chests(type: "Imc"),
        Chests(groupMore: "\"Condition\":{\"Group\":\"Belly\"}"),
        Chests(optionMore: "\"Condition\":{\"Group\":\"Belly\"}"),
        Chests(files: new() { ["chara/x.tex"] = "x.tex" }),
        Chests(count: 1),
        Chests(slot: "Legs"),
        Chests(extra: "\"Id\":5"),
    };

    [Theory]
    [MemberData(nameof(Unqualified))]
    [Trait("Plan", "EDGE-atr-chest-sizes-2")]
    public void Groups_that_do_not_qualify_add_no_chests(string chest)
    {
        using var mod = new Mod(Meta(null, Builds(), chest));
        Assert.All(mod.Catalog.For("_top"), o => Assert.False(BodySizeCatalog.IsShape(o)));
        Assert.Equal(4, mod.Catalog.For("_top").Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-2")]
    public void An_unreadable_manifest_adds_no_chests()
    {
        using var mod = new Mod(Nirae());
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"), Nirae()[..^3]);
        Assert.All(BodySizeCatalog.Read(mod.Root).For("_top"), o => Assert.False(BodySizeCatalog.IsShape(o)));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-3")]
    public void The_group_switching_most_chest_attributes_is_the_chest()
    {
        using var mod = new Mod(Meta(null, Builds(), Belly(), Chests()));
        Assert.All(mod.Catalog.For("_top"), o => Assert.Equal("Chest", o.Group));
        // Two of two attributes each: the earlier.
        using var tie = new Mod(Meta(null, Builds(), Belly(), Chests(count: 2, name: "Other")
                                                                  .Replace("atrx_c_large", "atrx_c_flat")));
        var manifest = BodyAtrSwitches.Manifest(tie.Root)!;
        Assert.Equal("Belly", BodyAtrSwitches.ShapeGroup(manifest, "Body")!.Group);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-4")]
    public void Legs_hands_and_feet_are_never_changed()
    {
        using var mod = new Mod(Meta(null, Builds(hands: true), Chests(), Belly()));
        Assert.Equal(4, mod.Catalog.For("_dwn").Count);
        Assert.Equal(4, mod.Catalog.For("_glv").Count);
        Assert.All(mod.Catalog.For("_dwn").Concat(mod.Catalog.For("_glv")), o => Assert.False(BodySizeCatalog.IsShape(o)));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-5")]
    public void Build_labels_trim_and_fall_back()
    {
        string Labelled(string a1, string a2, string b1, string b2)
        {
            using var mod = new Mod(Meta(null, Group("Size", "Single", 0, null,
                Option(a1, new() { [TopPath] = @"files\\top_a.mdl" }, null),
                Option(a2, new() { [TopPath] = @"files\\top_a.mdl" }, null),
                Option(b1, new() { [TopPath] = @"files\\top_b.mdl" }, null),
                Option(b2, new() { [TopPath] = @"files\\top_b.mdl" }, null)), Chests()));
            return string.Join("|", mod.Catalog.For("_top").Where(o => o.Name == "Flat").Select(o => o.Section));
        }
        Assert.Equal("X|Y", Labelled("X · S", "X · L", "Y · S", "Y · L"));
        Assert.Equal("X|Y", Labelled("X - S", "X - L", "Y - S", "Y - L"));
        Assert.Equal("S X|S Y", Labelled("S X", "L X", "S Y", "L Y"));
        Assert.Equal("X · S|X · M", Labelled("X · S", "X · L", "X · M", "X · XL"));

        using var one = new Mod(Meta(null, Group("Size", "Single", 0, null,
            Option("Only", new() { [TopPath] = @"files\\top_a.mdl" }, null)), Chests()));
        Assert.Equal(ChestNames, one.Catalog.For("_top").Select(o => o.Label));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-6")]
    public void Repeated_chest_names_list_once()
    {
        string twice = Chests().Replace("\"Options\":[", "\"Options\":[" + Option("Flat", null, null, Atr("atrx_c_flat", true)) + ",");
        using var mod = new Mod(Meta(null, Builds(), twice));
        Assert.Equal(6, mod.Catalog.For("_top").Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-7")]
    public void Default_data_chest_is_crossed()
    {
        using var mod = new Mod(Meta($"\"{TopPath}\":\"files\\\\top.mdl\"", Chests()));
        var top = mod.Catalog.For("_top");
        Assert.Equal(ChestNames, top.Select(o => o.Label));
        Assert.EndsWith(Path.Combine("files", "top.mdl"), mod.Catalog.PathOf(top[0]));
        var draw = RefitCore.MaskOf(mod.Root, mod.Catalog, "_top", null, 201)!.Onto(top[0]);
        Assert.Equal(["atrx_c_large", "atrx_c_perky"], draw.AtrOff.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-8")]
    public void Untold_chest_reads_the_default()
    {
        using var mod = new Mod(Nirae());
        var cat = mod.Catalog;
        foreach (var selected in new Dictionary<string, List<string>>?[] { null, new(), Pick("Chest", "Gone") })
            Assert.Equal("B · Large", RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_b.mdl"), selected)!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-9")]
    public void Unshaped_shared_file_worn_is_the_first_as_before()
    {
        using var mod = new Mod(Meta(null, Builds()));
        var cat = mod.Catalog;
        Assert.Equal("B · Small hips",
                     RefitCore.WornOption(cat, "_top", "0201", Wears(cat, "top_b.mdl"), Pick("Build & Hips", "B · Large hips"))!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-10")]
    public void Remembered_bare_rel_fills_the_ticked_chest()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        Assert.Equal("A · Flat", BodySizeCatalog.ByFile(top, @"files\top_a.mdl", Pick("Chest", "Flat"))!.Label);
        Assert.Equal("A · Large", BodySizeCatalog.ByFile(top, @"files\top_a.mdl", null)!.Label);
        Assert.Equal("A · Small Perky", BodySizeCatalog.ByFile(top, @"files\top_a.mdl|Small Perky", Pick("Chest", "Flat"))!.Label);
        Assert.Null(BodySizeCatalog.ByFile(top, @"files\gone.mdl", null));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-11")]
    public void First_pick_takes_the_default_chest()
    {
        using var mod = new Mod(Nirae());
        var body = new BodyModIndex.Entry("nir", "NIRÆ body", mod.Catalog);
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", body, "0201", canReadWorn: false);
        Assert.Equal(@"files\top_a.mdl|Large", pref.Chest!.Rel);

        var fill = new AutoRefitPreference();
        fill.SetBody("_top", "nir");
        fill.PendingFill["_top"] = true;
        AutoRefitDecisions.ApplySnapshot(fill, new AutoRefitTargets(fill, d => d == "nir" ? body : null, "0201"), null, out _);
        Assert.Equal(@"files\top_a.mdl|Large", fill.Chest!.Rel);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-12")]
    public void An_old_record_is_another_size()
    {
        using var mod = new Mod(Nirae());
        var flat = Entry(mod.Catalog, "A · Flat");
        Assert.NotEqual(RefitCore.Normal(@"files\top_a.mdl"), RefitCore.Normal(flat.Rel));
        Assert.False(AutoRefitDecisions.SameFile("nir", flat, "nir", flat with { Rel = @"files\top_a.mdl" }));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-13")]
    public void Bent_catalog_keeps_the_chests_apart()
    {
        using var mod = new Mod(Nirae());
        var entries = mod.Catalog.For("_top").Take(3).ToList();
        var bent = entries.Select(o => o with { Rel = "baked.mdl" + BodySizeCatalog.ShapeSuffix(o.Rel) }).ToList();
        Assert.Equal(3, bent.Select(o => o.Rel).Distinct().Count());
        Assert.All(bent, o => Assert.Equal("baked.mdl", BodySizeCatalog.FileOf(o.Rel)));
        Assert.Equal("", BodySizeCatalog.ShapeSuffix(@"files\top_a.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-14")]
    public void Legs_companion_of_a_chest_entry_is_of_its_build()
    {
        using var mod = new Mod(Nirae());
        var legs = mod.Catalog.For("_dwn");
        var chest = Entry(mod.Catalog, "B · Flat");
        BodyOption Leg(string label) => legs.Single(o => o.Label == label);
        Assert.Equal("B · Large hips", AutoRefitDecisions.Companion(legs, chest, Leg("B · Large hips"))!.Label);
        Assert.Equal("B · Small hips", AutoRefitDecisions.Companion(legs, chest, Leg("A · Large hips"))!.Label);
        Assert.Equal("B · Small hips", AutoRefitDecisions.Companion(legs, chest, null)!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-15")]
    public void Author_size_matches_by_model_file()
    {
        // The AuthorSize check compares model files: the ranking names a chest model by its first chest.
        using var mod = new Mod(Nirae());
        var first = Entry(mod.Catalog, "B · Flat");
        var target = Entry(mod.Catalog, "B · Small Perky");
        Assert.Equal(RefitCore.Normal(BodySizeCatalog.FileOf(first.Rel)), RefitCore.Normal(BodySizeCatalog.FileOf(target.Rel)));
        Assert.Equal(mod.Catalog.PathOf(first), mod.Catalog.PathOf(target));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-16")]
    public void Detection_still_leaves_out_shape_switching_bodies()
    {
        using var mod = new Mod(Nirae());
        Assert.True(BodyAtrSwitches.SwitchesShapes(mod.Root));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-17")]
    public void Picker_ids_are_unique()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        Assert.Equal(top.Count, top.Select(o => o.Label + "##" + o.Rel).Distinct().Count());
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-18")]
    public void Chest_entries_open_their_model_file()
    {
        using var mod = new Mod(Nirae());
        Assert.All(mod.Catalog.For("_top"), o => Assert.True(File.Exists(mod.Catalog.PathOf(o))));
        Assert.Equal(Path.Combine(mod.Root, "files", "top_b.mdl"),
                     BodySizeCatalog.ResolveCaseInsensitive(mod.Root, "FILES/TOP_B.MDL|Large"), ignoreCase: true);
        Assert.Equal(Path.Combine(mod.Root, "files", "top_b.mdl"), BodySizeCatalog.ResolveCaseInsensitive(mod.Root, "files/top_b.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-19")]
    public void Chest_companion_of_a_legs_anchor_is_of_its_build()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        var anchor = mod.Catalog.For("_dwn").Single(o => o.Label == "B · Large hips");
        Assert.Equal("B · Flat", AutoRefitDecisions.Companion(top, anchor, Entry(mod.Catalog, "B · Flat"))!.Label);
        Assert.Equal("B · Large", AutoRefitDecisions.Companion(top, anchor, Entry(mod.Catalog, "A · Flat"))!.Label);
        Assert.Equal("B · Large", AutoRefitDecisions.Companion(top, anchor, null)!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-20")]
    public void Companion_falls_back_to_the_default_chest_not_the_first()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        var hands = new BodyOption("Hands", "", "NIRÆ", @"files\glv.mdl", GlvPath, "_glv");
        Assert.Equal("A · Large", AutoRefitDecisions.Companion(top, hands, null)!.Label);
        Assert.Equal("A · Large", AutoRefitDecisions.FirstSize(top).Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-21")]
    public void Old_saved_chest_takes_the_ticked_chest_when_worn_is_another_build()
    {
        using var mod = new Mod(Nirae());
        var top = mod.Catalog.For("_top");
        var old = new BodySizeRef { Rel = @"files\top_b.mdl", Group = "Build & Hips", Section = "", Name = "B · Large hips" };
        Assert.Equal("B · Small Perky",
                     AutoRefitDecisions.Resolve(top, old, Entry(mod.Catalog, "A · Flat"), Pick("Chest", "Small Perky"))!.Label);

        var body = new BodyModIndex.Entry("nir", "NIRÆ body", mod.Catalog);
        var pref = new AutoRefitPreference();
        pref.SetBody("_top", "nir");
        pref.SetSize("_top", old);
        var targets = new AutoRefitTargets(pref, d => d == "nir" ? body : null, "0201")
            .With("0201", new Dictionary<string, BodyOption?>(), _ => Pick("Chest", "Small Perky"));
        Assert.Equal("B · Small Perky", targets.Chosen("_top")!.Label);
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-22")]
    public void Cli_chest_pair_is_built_for_another_chest_of_the_same_model()
    {
        using var mod = new Mod(Nirae());
        using var fork = new Mod(Nirae());
        var cat = mod.Catalog;
        var flat = Entry(cat, "A · Flat");
        Assert.False(RefitCli::Proteus.Refit.Program.SameChestBody(cat, flat, cat, Entry(cat, "A · Large")));
        Assert.True(RefitCli::Proteus.Refit.Program.SameChestBody(cat, flat, cat, flat));
        Assert.False(RefitCli::Proteus.Refit.Program.SameChestBody(cat, flat, fork.Catalog, Entry(fork.Catalog, "A · Flat")));
        using var plain = new Mod(Meta(null, Builds()));
        var p = plain.Catalog.For("_top");
        Assert.True(RefitCli::Proteus.Refit.Program.SameChestBody(plain.Catalog, p[0], plain.Catalog, p[1]));
    }

    [Fact]
    [Trait("Plan", "EDGE-atr-chest-sizes-23")]
    public void Companion_and_resolve_are_unchanged_without_chest_entries()
    {
        using var mod = new Mod(Meta(null, Builds()));
        var legs = mod.Catalog.For("_dwn");
        var top = mod.Catalog.For("_top");
        Assert.Equal("B · Large hips", AutoRefitDecisions.Companion(legs, top.Single(o => o.Name == "B · Large hips"), null)!.Label);
        var odd = new BodyOption("X", "", "Nothing", "x.mdl", TopPath, "_top");
        Assert.Equal(legs[1], AutoRefitDecisions.Companion(legs, odd, legs[1]));
        Assert.Equal(legs[0], AutoRefitDecisions.Companion(legs, odd, null));
        var saved = AutoRefitDecisions.RefOf(top[3]);
        Assert.Equal(AutoRefitDecisions.Resolve(top, saved), AutoRefitDecisions.Resolve(top, saved, top[0], Pick("Chest", "Flat")));
    }
}
