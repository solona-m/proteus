extern alias RefitCli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which of a body model's custom attributes its mod's <c>Atr</c> options switch off (<see cref="BodyAtrSwitches"/>),
/// read the way Penumbra applies them: NIRÆ picks one of nine chests in one model this way, and a refit has to see only
/// the one drawn.
/// </summary>
public class BodyAtrSwitchesTests
{
    // ── manifests, written by hand ──────────────────────────────────────────────────────────────────────

    private static string Atr(string attribute, bool on, string? slot = "Body", string? extra = null)
        => "{\"Type\":\"Atr\",\"Manipulation\":{" + (slot != null ? $"\"Slot\":\"{slot}\"," : "")
           + $"\"Attribute\":\"{attribute}\",\"Entry\":{(on ? "true" : "false")}" + (extra != null ? "," + extra : "") + "}}";

    private static string Option(string name, params string[] manips) => Option(name, 0, null, manips);

    private static string Option(string name, int priority, string? more, params string[] manips)
        => $"{{\"Name\":\"{name}\",\"Priority\":{priority},\"Files\":{{}}," + (more != null ? more + "," : "")
           + $"\"Manipulations\":[{string.Join(",", manips)}]}}";

    private static string Group(string name, string type, int defaultSettings, int priority, params string[] options)
        => GroupWith(name, type, defaultSettings, priority, null, options);

    private static string GroupWith(string name, string type, int defaultSettings, int priority, string? more,
                                    params string[] options)
        => $"{{\"Name\":\"{name}\",\"Type\":\"{type}\",\"Priority\":{priority},\"DefaultSettings\":{defaultSettings},"
           + (more != null ? more + "," : "") + $"\"Options\":[{string.Join(",", options)}]}}";

    private static string Meta(string[] defaults, params string[] groups)
        => "{\"FileVersion\":4,\"Name\":\"Body\",\"Author\":\"x\",\"DefaultData\":{\"Files\":{},\"Manipulations\":["
           + string.Join(",", defaults) + "]},\"Groups\":[" + string.Join(",", groups) + "]}";

    /// <summary>A three-chest group, each option drawing its own chest and switching the others off.</summary>
    private static string Chests(int defaultSettings = 0, int priority = 0, string name = "Chest")
    {
        string[] chests = ["atrx_c_flat", "atrx_c_perky", "atrx_c_large"];
        string[] names = ["Flat", "Small Perky", "Large"];
        return Group(name, "Single", defaultSettings, priority,
                     names.Select((n, i) => Option(n, chests.Select((c, j) => Atr(c, i == j)).ToArray())).ToArray());
    }

    private sealed class Mod : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "proteus-atr-" + Guid.NewGuid().ToString("N"));

        public Mod(string meta)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "meta.json"), meta);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    private static BodyAtrSwitches.Result? Resolve(string meta, IReadOnlyDictionary<string, List<string>>? selected = null,
                                                   string slot = "Body", ushort? race = 201, BodyOption? onto = null)
    {
        using var mod = new Mod(meta);
        var manifest = BodyAtrSwitches.Manifest(mod.Root);
        Assert.NotNull(manifest);
        return BodyAtrSwitches.Resolve(new BodyAtrSwitches.Query(manifest!, slot, selected, race), onto);
    }

    private static Dictionary<string, List<string>> Pick(string group, params string[] options)
        => new() { [group] = [.. options] };

    private static string[] Off(BodyAtrSwitches.Result? r) => r?.Off.Order(StringComparer.Ordinal).ToArray() ?? [];

    // ── the choice ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-2")]
    public void A_single_choice_switches_its_other_alternatives_off()
    {
        var r = Resolve(Meta([], Chests()), Pick("Chest", "Small Perky"));
        Assert.Equal(["atrx_c_flat", "atrx_c_large"], Off(r));
        Assert.Equal(["atrx_c_flat", "atrx_c_large", "atrx_c_perky"], r!.Switched.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-6")]
    public void Null_selection_reads_the_defaults()
    {
        string meta = Meta([], Chests(defaultSettings: 2));
        Assert.Equal(["atrx_c_flat", "atrx_c_perky"], Off(Resolve(meta)));
        using var mod = new Mod(meta);
        var draw = RefitCore.MaskOf(mod.Root, BodySizeCatalog.Read(mod.Root), "_top", null, 201);
        Assert.Equal(["atrx_c_flat", "atrx_c_perky"], draw!.AtrOff.Order(StringComparer.Ordinal));
        Assert.Null(draw.ImcMask);
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-6")]
    public void The_cli_reads_the_body_mod_s_defaults()
    {
        using var mod = new Mod(Meta([], Chests(defaultSettings: 1)));
        var catalog = BodySizeCatalog.Read(mod.Root);
        var cli = RefitCli::Proteus.Refit.Program.MaskOf(catalog, "_top", "0201");
        Assert.NotNull(cli);
        Assert.Equal(RefitCore.MaskOf(mod.Root, catalog, "_top", null, 201), cli);
        Assert.Equal(["atrx_c_flat", "atrx_c_large"], cli!.AtrOff.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-5")]
    public void The_auto_refit_reads_the_player_s_settings()
    {
        using var mod = new Mod(Meta([], Chests(defaultSettings: 2)));
        string dir = Path.GetFileName(mod.Root);
        var settings = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>
        {
            [dir] = new(true, 0, Pick("Chest", "Small Perky"), Inherited: false, Temporary: false),
        };
        var draw = RefitCore.MaskOf(mod.Root, BodySizeCatalog.Read(mod.Root), "_top",
                                    AutoRefitWatcher.OptionsOf(settings, dir), 201);
        Assert.Equal(["atrx_c_flat", "atrx_c_large"], draw!.AtrOff.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-9")]
    public void The_option_refitted_onto_counts_as_chosen()
    {
        // A size option that also switches: the user's selection has the large hips, the size refitted onto the small.
        string meta = Meta([], Group("Build", "Single", 0, 0,
            Option("Small hips", Atr("atrx_h_s", true), Atr("atrx_h_l", false)),
            Option("Large hips", Atr("atrx_h_s", false), Atr("atrx_h_l", true))));
        using var mod = new Mod(meta);
        var manifest = BodyAtrSwitches.Manifest(mod.Root)!;
        var query = new BodyAtrSwitches.Query(manifest, "Body", Pick("Build", "Large hips"), 201);
        var onto = new BodyOption("Build", "", "Small hips", "files/small.mdl", "chara/body.mdl", "_top");

        Assert.Equal(["atrx_h_s"], Off(BodyAtrSwitches.Resolve(query)));
        Assert.Equal(["atrx_h_l"], Off(BodyAtrSwitches.Resolve(query, onto)));
        var draw = new BodyRetarget.BodyDraw(0b101, new HashSet<string> { "atrx_h_s" }, new HashSet<string>(), query);
        var taken = draw.Onto(onto);
        Assert.Equal((ushort?)0b101, taken.ImcMask);   // the IMC part is the slot's, never overlaid
        Assert.Equal(["atrx_h_l"], taken.AtrOff);
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-7")]
    public void A_mod_with_only_an_imc_group_gives_its_mask_and_no_switches()
    {
        string meta = "{\"FileVersion\":4,\"Name\":\"Body\",\"Author\":\"x\",\"Groups\":[{\"Name\":\"Shins\",\"Type\":\"Imc\","
                      + "\"Priority\":0,\"DefaultSettings\":0,\"Identifier\":{\"ObjectType\":\"Equipment\",\"PrimaryId\":0,"
                      + "\"EquipSlot\":\"Body\",\"Variant\":1},\"DefaultEntry\":{\"AttributeMask\":1},"
                      + "\"Options\":[{\"Name\":\"Thick\",\"AttributeMask\":3}]}]}";
        using var mod = new Mod(meta);
        var catalog = BodySizeCatalog.Read(mod.Root);
        var draw = RefitCore.MaskOf(mod.Root, catalog, "_top", null, 201);
        Assert.Equal(ImcEntrySource.MaskFor(mod.Root, 0, "Body", null), draw!.ImcMask);
        Assert.NotNull(draw.ImcMask);
        Assert.Empty(draw.AtrOff);
        Assert.Empty(draw.AtrSwitched);
        Assert.Equal(draw, draw.Onto(new BodyOption("Shins", "", "Thick", "a.mdl", "b.mdl", "_top")));

        using var bare = new Mod(Meta([]));
        Assert.Null(RefitCore.MaskOf(bare.Root, BodySizeCatalog.Read(bare.Root), "_top", null, 201));
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-7")]
    [Trait("Plan", "EDGE-body-atr-switches-24")]
    public void BodyDraw_equality_is_by_value()
    {
        var a = new BodyRetarget.BodyDraw(3, new HashSet<string> { "atrx_a", "atrx_b" }, new HashSet<string> { "atrx_c" });
        using var mod = new Mod(Meta([], Chests()));
        var query = new BodyAtrSwitches.Query(BodyAtrSwitches.Manifest(mod.Root)!, "Body", null, 201);
        var b = new BodyRetarget.BodyDraw(3, new SortedSet<string> { "atrx_b", "atrx_a" }, new HashSet<string> { "atrx_c" }, query);
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { ImcMask = 1 });
        Assert.NotEqual(a, a with { AtrOff = new HashSet<string> { "atrx_a", "atrx_b", "atrx_x" } });
        Assert.NotEqual(a, a with { AtrSwitched = new HashSet<string> { "atrx_c", "atrx_x" } });

        var catalog = BodySizeCatalog.Read(mod.Root);
        Assert.Equal(RefitCore.MaskOf(mod.Root, catalog, "_top", null, 201), RefitCore.MaskOf(mod.Root, catalog, "_top", null, 201));
    }

    // ── Penumbra's rules ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-2")]
    public void A_judged_name_only_an_unticked_option_mentions_stays_drawn_but_is_switched()
    {
        var r = Resolve(Meta([], Group("Extra", "Single", 0, 0, Option("None"), Option("Tail", Atr("atrx_tail", false)))));
        Assert.DoesNotContain("atrx_tail", r!.Off);
        Assert.Contains("atrx_tail", r.Switched);
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-3")]
    public void Defaults_by_group_type_and_clamp()
    {
        // Single: past the last option is the last (SingleModGroup.FixSetting).
        Assert.Equal(["atrx_c_flat", "atrx_c_perky"], Off(Resolve(Meta([], Chests(defaultSettings: 9)))));
        // Absent DefaultSettings: the first.
        string noDefault = Meta([], Chests()).Replace("\"DefaultSettings\":0,", "");
        Assert.Equal(["atrx_c_large", "atrx_c_perky"], Off(Resolve(noDefault)));
        // No options: nothing.
        Assert.Null(Resolve(Meta([], Group("Empty", "Single", 0, 0))));
        // Multi: the bitmask, every ticked option applied.
        string multi = Meta([], Group("Bits", "Multi", 0b10, 0,
                                      Option("A", Atr("atrx_a", false)), Option("B", Atr("atrx_b", false))));
        Assert.Equal(["atrx_b"], Off(Resolve(multi)));
        // Names by any case.
        Assert.Equal(["atrx_c_flat", "atrx_c_large"], Off(Resolve(Meta([], Chests()), Pick("CHEST", "small perky"))));
        // A choice the group does not have: no say, left as it was.
        var unknown = Resolve(Meta([], Chests()), Pick("Chest", "Gone"));
        Assert.Empty(Off(unknown));
        Assert.Null(unknown);
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-4")]
    public void Penumbra_precedence()
    {
        string Two(int p1, int p2) => Meta([Atr("atrx_x", true)],
            Group("First", "Single", 0, p1, Option("On", Atr("atrx_x", true))),
            Group("Second", "Single", 0, p2, Option("Off", Atr("atrx_x", false))));
        // A higher priority wins; a tie goes to the later group; the default data comes last.
        Assert.Empty(Off(Resolve(Two(5, 0))));
        Assert.Equal(["atrx_x"], Off(Resolve(Two(0, 5))));
        Assert.Equal(["atrx_x"], Off(Resolve(Two(0, 0))));
        Assert.Equal(["atrx_x"], Off(Resolve(Meta([Atr("atrx_x", true)], Group("G", "Single", 0, 0, Option("Off", Atr("atrx_x", false)))))));
        // Multi: by option priority, a tie to the earlier option (MultiModGroup).
        string multi(int p1, int p2) => Meta([], Group("M", "Multi", 0b11, 0,
            Option("On", p1, null, Atr("atrx_x", true)), Option("Off", p2, null, Atr("atrx_x", false))));
        Assert.Empty(Off(Resolve(multi(0, 0))));
        Assert.Equal(["atrx_x"], Off(Resolve(multi(0, 1))));
        // Within one option, the first of two entries for one name wins.
        Assert.Equal(["atrx_x"], Off(Resolve(Meta([], Group("G", "Single", 0, 0,
            Option("Both", Atr("atrx_x", false), Atr("atrx_x", true)))))));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-5")]
    public void CheckEntry_tier_order()
    {
        string With(params string[] entries) => Meta([], Group("G", "Single", 0, 0, Option("O", entries)));
        string race = "\"GenderRaceCondition\":201";
        // (slot, set, race) over (slot, set, any race)
        Assert.Empty(Off(Resolve(With(Atr("atrx_x", true, "Body", "\"Id\":0," + race), Atr("atrx_x", false, "Body", "\"Id\":0")))));
        // (slot, set, any race) over (slot, no set, race)
        Assert.Empty(Off(Resolve(With(Atr("atrx_x", true, "Body", "\"Id\":0"), Atr("atrx_x", false, "Body", race)))));
        // (slot, no set, race) over (any slot, no set, race)
        Assert.Empty(Off(Resolve(With(Atr("atrx_x", true, "Body", race), Atr("atrx_x", false, null, race)))));
        // (any slot, no set, race) over (slot, no set, any race)
        Assert.Empty(Off(Resolve(With(Atr("atrx_x", true, null, race), Atr("atrx_x", false, "Body")))));
        // (slot, no set, any race) over (any slot, no set, any race)
        Assert.Empty(Off(Resolve(With(Atr("atrx_x", true, "Body"), Atr("atrx_x", false, null)))));
        // The last tier on its own
        Assert.Equal(["atrx_x"], Off(Resolve(With(Atr("atrx_x", false, null)))));
        // Another slot's entry never reaches the body's
        Assert.Empty(Off(Resolve(With(Atr("atrx_x", false, "Hands")))));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-6")]
    public void Set_zero_and_no_id_apply_and_another_set_does_not()
    {
        string With(string extra) => Meta([], Group("G", "Single", 0, 0, Option("O", Atr("atrx_x", false, "Body", extra))));
        Assert.Equal(["atrx_x"], Off(Resolve(With("\"Id\":0"))));
        Assert.Empty(Off(Resolve(With("\"Id\":504"))));
        Assert.Equal(["atrx_x"], Off(Resolve(Meta([], Group("G", "Single", 0, 0, Option("O", Atr("atrx_x", false)))))));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-7")]
    public void Race_entries_judged_against_the_target_race()
    {
        string meta = Meta([], Group("G", "Single", 0, 0,
                                     Option("O", Atr("atrx_x", false, "Body", "\"GenderRaceCondition\":201"))));
        Assert.Equal(["atrx_x"], Off(Resolve(meta, race: 201)));
        Assert.Empty(Off(Resolve(meta, race: 801)));
        // No race to judge by: no say, the tag kept.
        var unknown = Resolve(meta, race: null);
        Assert.Null(unknown);
        // A race Penumbra does not judge: nothing is off.
        Assert.Empty(Off(Resolve(Meta([], Group("G", "Single", 0, 0, Option("O", Atr("atrx_x", false)))), race: 104)));
    }

    [Theory]
    [Trait("Plan", "EDGE-body-atr-switches-7")]
    [InlineData("0201", (ushort)201)]
    [InlineData("0801", (ushort)801)]
    [InlineData("c0201", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("-201", null)]
    public void RaceNumber_reads_a_model_race_code(string? code, ushort? expected)
        => Assert.Equal(expected, RefitCore.RaceNumber(code));

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-6")]
    public void The_cli_judges_race_limited_entries_by_its_race()
    {
        // A switch limited to Midlander women: the CLI's "0201" judges it, as RefitCore.MaskOf does with 201.
        using var mod = new Mod(Meta([], Group("G", "Single", 0, 0,
                                               Option("O", Atr("atrx_x", false, "Body", "\"GenderRaceCondition\":201")))));
        var catalog = BodySizeCatalog.Read(mod.Root);
        var cli = RefitCli::Proteus.Refit.Program.MaskOf(catalog, "_top", "0201");
        Assert.Equal(["atrx_x"], cli!.AtrOff);
        Assert.Equal(RefitCore.MaskOf(mod.Root, catalog, "_top", null, 201), cli);
    }

    [Theory]
    [Trait("Plan", "EDGE-body-atr-switches-22")]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void A_kept_end_refit_takes_its_size_as_chosen_only_when_it_is_the_one(int targets, bool overlaid)
    {
        var options = Enumerable.Range(0, targets).Select(i => new BodyOption("Size", "", $"S{i}", $"{i}.mdl", "g.mdl", "_sho"))
                                .ToList();
        Assert.Equal(overlaid ? options[0] : null, RetargetInputs.OverlayFor(options));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-8")]
    public void Non_atrx_names_are_ignored()
        => Assert.Null(Resolve(Meta([], Group("G", "Single", 0, 0, Option("O", Atr("atr_ude", false), Atr("shpx_x", false))))));

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-9")]
    public void Unreadable_entries_give_their_name_no_say()
    {
        string With(string entry) => Meta([], Group("G", "Single", 0, 0, Option("O", Atr("atrx_ok", false), entry)));
        string[] bad =
        [
            Atr("atrx_x", false, "Body", "\"Id\":\"0\""),                     // a string where Penumbra reads a number
            Atr("atrx_x", false, "Body", "\"GenderRaceCondition\":\"201\""),
            Atr("atrx_x", false, "Body", "\"GenderRaceCondition\":202"),      // not a race Penumbra knows
            "{\"Type\":\"Atr\",\"Manipulation\":{\"Slot\":3,\"Attribute\":\"atrx_x\",\"Entry\":false}}",
            Atr("atrx_x", false, "UnkBonus"),
            Atr("atrx_x", false, null, "\"Id\":0"),                          // an id with no slot
            "{\"Type\":\"Atr\",\"Manipulation\":{\"Slot\":\"Body\",\"Attribute\":\"atrx_x\"}}",
        ];
        foreach (string entry in bad)
        {
            var r = Resolve(With(entry));
            Assert.Equal(["atrx_ok"], Off(r));
            Assert.DoesNotContain("atrx_x", r!.Switched);
        }
        using var corrupt = new Mod("{ not json");
        Assert.Null(BodyAtrSwitches.Manifest(corrupt.Root));
        Assert.Null(RefitCore.MaskOf(corrupt.Root, BodySizeCatalog.Read(corrupt.Root), "_top", null, 201));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-10")]
    public void Conditional_and_combining_owners_give_no_say()
    {
        string conditional = Meta([], Chests(), GroupWith("Gated", "Single", 0, 0, "\"Condition\":{\"Group\":\"Chest\"}",
                                                      Option("O", Atr("atrx_c_flat", true))));
        var r = Resolve(conditional, Pick("Chest", "Small Perky"));
        Assert.Equal(["atrx_c_large"], Off(r));       // the gated name keeps its say: none
        Assert.DoesNotContain("atrx_c_flat", r!.Switched);

        string option = Meta([], Group("G", "Single", 0, 0, Option("O", 0, "\"Condition\":{}", Atr("atrx_x", false))));
        Assert.Null(Resolve(option));

        string combining = Meta([], Chests(), GroupWith("Mix", "Combining", 0, 0,
            "\"Containers\":[{\"Files\":{},\"Manipulations\":[" + Atr("atrx_c_large", true) + "]}]", Option("A")));
        var c = Resolve(combining, Pick("Chest", "Small Perky"));
        Assert.Equal(["atrx_c_flat"], Off(c));
        Assert.DoesNotContain("atrx_c_large", c!.Switched);
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-11")]
    public void A_v3_folder_resolves_as_its_v4_copy()
    {
        using var v4 = new Mod(Meta([Atr("atrx_d", false)], Chests(defaultSettings: 1)));
        using var v3 = new Mod("{\"FileVersion\":3,\"Name\":\"Body\",\"Author\":\"x\"}");
        File.WriteAllText(Path.Combine(v3.Root, "default_mod.json"), "{\"Files\":{},\"Manipulations\":[" + Atr("atrx_d", false) + "]}");
        File.WriteAllText(Path.Combine(v3.Root, "group_001_chest.json"), Chests(defaultSettings: 1));
        BodyAtrSwitches.Result? Of(Mod m) => BodyAtrSwitches.Resolve(new BodyAtrSwitches.Query(BodyAtrSwitches.Manifest(m.Root)!, "Body", null, 201));
        var a = Of(v4);
        var b = Of(v3);
        Assert.Equal(["atrx_c_flat", "atrx_c_large", "atrx_d"], Off(a));
        Assert.Equal(Off(a), Off(b));
        Assert.True(a!.Switched.SetEquals(b!.Switched));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-12")]
    public void The_game_s_body_and_a_folder_without_a_manifest_have_no_say()
    {
        Assert.Null(RefitCore.MaskOf(VanillaBodyCatalog.Key, null, "_top", null, 201));
        string empty = Path.Combine(Path.GetTempPath(), "proteus-atr-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Null(BodyAtrSwitches.Manifest(empty));
            Assert.Null(RefitCore.MaskOf(empty, BodySizeCatalog.Read(empty), "_top", null, 201));
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-17")]
    public void An_edited_manifest_is_read_again_and_parallel_reads_agree()
    {
        using var mod = new Mod(Meta([], Chests(defaultSettings: 0)));
        Assert.Equal(["atrx_c_large", "atrx_c_perky"], Off(BodyAtrSwitches.Resolve(
            new BodyAtrSwitches.Query(BodyAtrSwitches.Manifest(mod.Root)!, "Body", null, 201))));

        File.WriteAllText(Path.Combine(mod.Root, "meta.json"), Meta([], Chests(defaultSettings: 2)) + "  ");
        var manifest = BodyAtrSwitches.Manifest(mod.Root)!;
        var query = new BodyAtrSwitches.Query(manifest, "Body", null, 201);
        Assert.Equal(["atrx_c_flat", "atrx_c_perky"], Off(BodyAtrSwitches.Resolve(query)));

        var results = new string[32][];
        Parallel.For(0, 32, i => results[i] = Off(BodyAtrSwitches.Resolve(
            new BodyAtrSwitches.Query(BodyAtrSwitches.Manifest(mod.Root)!, "Body", null, 201))));
        Assert.All(results, r => Assert.Equal(["atrx_c_flat", "atrx_c_perky"], r));

        // Onto reads no file: the manifest it was made from stands.
        var draw = new BodyRetarget.BodyDraw(null, new HashSet<string>(), new HashSet<string>(), query);
        File.Delete(Path.Combine(mod.Root, "meta.json"));
        Assert.Equal(["atrx_c_flat", "atrx_c_large"],
                     draw.Onto(new BodyOption("Chest", "", "Small Perky", "a.mdl", "b.mdl", "_top")).AtrOff.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-22")]
    public void Overlay_by_name_and_file()
    {
        // Two options of one name, told apart by their file: Neolithe's eight "SFW M" under eight headings.
        string Opt(string name, string file, string attr) =>
            $"{{\"Name\":\"{name}\",\"Files\":{{\"chara/equipment/e0000/model/c0201e0000_top.mdl\":\"{file}\"}},"
            + $"\"Manipulations\":[{Atr("atrx_p", attr == "atrx_p")},{Atr("atrx_q", attr == "atrx_q")}]}}";
        // The default is the second "SFW M" (draws q), so the overlay is seen whenever it picks the first.
        string Sized(string group) => Meta([], "{\"Name\":\"" + group + "\",\"Type\":\"Single\",\"Priority\":0,\"DefaultSettings\":1,\"Options\":["
                               + Opt("SFW M", @"files\\p.mdl", "atrx_p") + "," + Opt("SFW M", @"files\\q.mdl", "atrx_q") + "]}");
        string meta = Sized("Size");
        var byFile = new BodyOption("Size", "P", "SFW M", "Files/P.mdl", "chara/equipment/e0000/model/c0201e0000_top.mdl", "_top");
        Assert.Equal(["atrx_p"], Off(Resolve(meta)));                        // the default: q drawn
        Assert.Equal(["atrx_q"], Off(Resolve(meta, onto: byFile)));          // p, by its file
        Assert.Equal(["atrx_p"], Off(Resolve(meta, onto: byFile with { Rel = "files/q.mdl" })));
        // No file match: the first of the name, p.
        Assert.Equal(["atrx_q"], Off(Resolve(meta, onto: byFile with { Rel = "files/other.mdl" })));
        // No name match: no overlay, the default stands.
        Assert.Equal(["atrx_p"], Off(Resolve(meta, onto: byFile with { Name = "SFW L" })));
        // The "no options" group a catalog gives a mod without any: never overlaid, even on a group of that name.
        string pseudo = Sized(BodySizeCatalog.DefaultGroupName);
        Assert.Equal(["atrx_p"], Off(Resolve(pseudo, onto: byFile with { Group = BodySizeCatalog.DefaultGroupName })));

        // Multi: the option refitted onto is added to the ticked ones.
        string multi = Meta([], Group("Extras", "Multi", 0b01, 0, Option("A", Atr("atrx_a", false)), Option("B", Atr("atrx_b", false))));
        var b = new BodyOption("Extras", "", "B", "b.mdl", "g.mdl", "_top");
        Assert.Equal(["atrx_a", "atrx_b"], Off(Resolve(multi, onto: b)));
    }
}
