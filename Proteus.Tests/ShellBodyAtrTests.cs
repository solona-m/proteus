using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A second skin over a body that picks its shapes by option — NIRÆ draws one of its chests, chosen by its Chest
/// option's <c>Atr</c> switches, out of one chest model carrying all of them. The shell must cover only the chest drawn
/// (<see cref="ShellBodyAtr"/>), and follow it when the choice changes.
/// </summary>
public class ShellBodyAtrTests
{
    // ── a body mod with three chests, written by hand ─────────────────────────────────────────────────────

    private static readonly string[] ChestAttrs = ["atrx_c_flat", "atrx_c_perky", "atrx_c_large"];

    private static string Atr(string attribute, bool on, string slot = "Body", string? extra = null)
        => "{\"Type\":\"Atr\",\"Manipulation\":{" + $"\"Slot\":\"{slot}\","
           + $"\"Attribute\":\"{attribute}\",\"Entry\":{(on ? "true" : "false")}" + (extra != null ? "," + extra : "") + "}}";

    private static string Option(string name, params string[] manips)
        => $"{{\"Name\":\"{name}\",\"Priority\":0,\"Files\":{{}},\"Manipulations\":[{string.Join(",", manips)}]}}";

    private static string Group(string name, int defaultSettings, params string[] options)
        => $"{{\"Name\":\"{name}\",\"Type\":\"Single\",\"Priority\":0,\"DefaultSettings\":{defaultSettings},"
           + $"\"Options\":[{string.Join(",", options)}]}}";

    /// <summary>Flat, Small Perky, Large: each draws its own chest and switches the others off. Large by default.</summary>
    private static string Chests(string? race = null)
    {
        string[] names = ["Flat", "Small Perky", "Large"];
        return Group("Chest", 2, names.Select((n, i) =>
            Option(n, ChestAttrs.Select((c, j) => Atr(c, i == j, "Body", race)).ToArray())).ToArray());
    }

    /// <summary>Nipples on the chest: soft or inverted, soft by default.</summary>
    private static string Nipples(string? race = null)
        => Group("Nipples", 0,
            Option("Soft", Atr("atrx_n_soft", true, "Body", race), Atr("atrx_n_inv", false, "Body", race)),
            Option("Inverted", Atr("atrx_n_soft", false, "Body", race), Atr("atrx_n_inv", true, "Body", race)));

    /// <summary>The legs: closed or open, closed by default.</summary>
    private static string Vulva()
        => Group("Vulva", 0,
            Option("Closed", Atr("atrx_v_closed", true, "Legs"), Atr("atrx_v_open", false, "Legs")),
            Option("Open", Atr("atrx_v_closed", false, "Legs"), Atr("atrx_v_open", true, "Legs")));

    private static string Meta(params string[] groups)
        => "{\"FileVersion\":4,\"Name\":\"Body\",\"Author\":\"x\",\"DefaultData\":{\"Files\":{},\"Manipulations\":[]},"
           + "\"Groups\":[" + string.Join(",", groups) + "]}";

    /// <summary>A mods root holding one body mod, "NIRAE", with model files under it.</summary>
    private sealed class Mods : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "proteus-shellatr-" + Guid.NewGuid().ToString("N"));
        public const string Dir = "NIRAE";
        public string ModRoot => Path.Combine(Root, Dir);
        public string Top => Path.Combine(ModRoot, "body", "c0201e0000_top.mdl");
        public string Dwn => Path.Combine(ModRoot, "body", "c0201e0000_dwn.mdl");

        public Mods(string meta)
        {
            Directory.CreateDirectory(Path.Combine(ModRoot, "body"));
            File.WriteAllText(Path.Combine(ModRoot, "meta.json"), meta);
            File.WriteAllBytes(Top, new byte[100]);
            File.WriteAllBytes(Dwn, new byte[50]);
        }

        public void Rewrite(string meta)
        {
            var path = Path.Combine(ModRoot, "meta.json");
            File.WriteAllText(path, meta);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    /// <summary>Settings as Penumbra would report them, counting the reads.</summary>
    private sealed class Settings(Dictionary<string, List<string>>? options = null, bool enabled = true)
    {
        public int Calls;
        public Dictionary<string, List<string>> Options = options ?? new();
        public bool Enabled = enabled;
        public bool Unreadable;
        public bool Throws;

        public ShellBodyAtr.ModSettings? Of(string modDir)
        {
            Calls++;
            if (Throws) throw new InvalidOperationException("boom");
            if (Unreadable) return null;
            return new ShellBodyAtr.ModSettings(Enabled, Options);
        }
    }

    private static Dictionary<string, List<string>> Pick(string group, string option) => new() { [group] = [option] };

    private static ShellBodyAtr.PartAtr Top(Mods m, Settings s, ushort? race = 201, bool bare = true)
        => ShellBodyAtr.ForPart("top", bare, m.Top, m.Root, race, s.Of);

    // ── a three-chest body, built into a shell ────────────────────────────────────────────────────────────

    private static byte[] ThreeChests(params string[] extraAttrs)
        => SyntheticModel.Build([.. ChestAttrs, .. extraAttrs],
            new SyntheticModel.Mesh("/mt_c0201b0001_bibo.mtrl",
                [new SyntheticModel.Sub(1u << 0), new SyntheticModel.Sub(1u << 1), new SyntheticModel.Sub(1u << 2),
                 .. extraAttrs.Select((_, i) => new SyntheticModel.Sub(1u << (3 + i)))]));

    private static byte[] Shell(byte[] body, IReadOnlySet<string>? hidden, int layers = 1)
        => SecondSkinWriter.Build(
            [new SecondSkinWriter.SourceSpec(body, HiddenAttributes: hidden)],
            Enumerable.Range(0, layers).Select(i => new SecondSkinLayer
                { MaterialName = $"/mt_c0201a0053_rir_{(char)('a' + i)}.mtrl", Coverage = null }).ToArray(),
            null, out _);

    /// <summary>The attribute names the drawn triangles of a built model carry, once per triangle.</summary>
    private static List<string> DrawnTags(byte[] built)
    {
        var parts = ModelPartReader.Read(built)!;
        var tags = new List<string>();
        foreach (var p in parts.Parts.Where(p => p.TriangleCount > 0))
            for (int bit = 0; bit < 32 && bit < parts.AttributeNames.Count; bit++)
                if ((p.AttributeMask & (1u << bit)) != 0) tags.Add(parts.AttributeNames[bit]);
        return tags;
    }

    // ── AC-1: only the selected chest is covered ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-1")]
    public void Shell_keeps_only_the_selected_chest()
    {
        using var m = new Mods(Meta(Chests()));
        var s = new Settings(Pick("Chest", "Small Perky"));
        var atr = Top(m, s);
        Assert.Equal(["atrx_c_flat", "atrx_c_large"], atr.Off!.Order(StringComparer.Ordinal));

        var body = ThreeChests();
        var covered = DrawnTags(Shell(body, ShellBodyAtr.Merge(null, atr.Off)));
        Assert.Contains("atrx_c_perky", covered);
        Assert.DoesNotContain("atrx_c_flat", covered);
        Assert.DoesNotContain("atrx_c_large", covered);

        // Today's bug, pinned: without the options' say, all three chests are covered.
        var all = DrawnTags(Shell(body, null));
        Assert.All(ChestAttrs, c => Assert.Contains(c, all));
    }

    // ── AC-2: every switch, each slot by its own options ──────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-2")]
    public void Every_switch_on_every_bare_slot_is_hidden()
    {
        using var m = new Mods(Meta(Chests(), Nipples(), Vulva()));
        var s = new Settings(new() { ["Chest"] = ["Flat"], ["Nipples"] = ["Inverted"], ["Vulva"] = ["Open"] });

        var top = Top(m, s).Off!;
        Assert.Contains("atrx_n_soft", top);
        Assert.DoesNotContain("atrx_n_inv", top);
        Assert.DoesNotContain(top, a => a.StartsWith("atrx_v_", StringComparison.Ordinal));

        var dwn = ShellBodyAtr.ForPart("dwn", true, m.Dwn, m.Root, 201, s.Of).Off!;
        Assert.Contains("atrx_v_closed", dwn);
        Assert.DoesNotContain("atrx_v_open", dwn);
        Assert.DoesNotContain(dwn, a => a.StartsWith("atrx_c_", StringComparison.Ordinal)
                                     || a.StartsWith("atrx_n_", StringComparison.Ordinal));
    }

    // ── AC-3: a changed choice is noticed and acted on ────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-3")]
    public void A_changed_chest_is_moved()
    {
        using var m = new Mods(Meta(Chests()));
        var large = Top(m, new Settings()).Use!;
        Assert.True(ShellBodyAtr.Moved([large], Mods.Dir, new Settings(Pick("Chest", "Small Perky")).Of,
                                       out var why, out var target));
        Assert.Contains("top", why);
        Assert.Contains("atrx_c_flat,atrx_c_large", target);
        Assert.Contains("atrx_c_flat,atrx_c_perky", why);   // the old key, named
    }

    [Theory]
    [Trait("Plan", "AC-nirae-shell-chest-3")]
    [Trait("Plan", "EDGE-nirae-shell-chest-10")]
    [Trait("Plan", "EDGE-nirae-shell-chest-21")]
    //        surface affects check  baseMoved shapes force | verdict      Force  Invalidate Recheck
    [InlineData(false, false, true,  false,    false, true,  false, false, false, false)]
    [InlineData(false, false, true,  false,    true,  false, true,  true,  false, false)]
    [InlineData(true,  false, true,  false,    false, true,  false, false, false, false)]
    [InlineData(true,  false, true,  false,    true,  false, true,  true,  false, false)]
    [InlineData(true,  true,  true,  false,    false, true,  false, false, false, true)]
    [InlineData(true,  true,  true,  false,    true,  false, true,  true,  false, true)]
    [InlineData(true,  true,  true,  true,     false, false, true,  false, true,  false)]
    [InlineData(true,  true,  true,  true,     false, true,  true,  true,  true,  false)]
    [InlineData(true,  true,  true,  true,     true,  false, true,  true,  true,  false)]
    [InlineData(true,  true,  false, false,    false, true,  true,  true,  true,  false)]
    public void Setting_decision_table(bool surface, bool affects, bool check, bool baseMoved, bool shapes, bool force,
                                       bool recomposite, bool expectForce, bool invalidate, bool recheck)
    {
        var d = CompositorService.DecideSettingChange(surface, affects, check, baseMoved, shapes, force);
        Assert.Equal(recomposite ? CompositorService.SettingVerdict.Recomposite : CompositorService.SettingVerdict.Ignore,
                     d.Verdict);
        if (recomposite) Assert.Equal(expectForce, d.Force);
        Assert.Equal(invalidate, d.InvalidateUpstreams);
        Assert.Equal(recheck, d.ScheduleRecheck);
    }

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-3")]
    public void Evaluation_and_window_call_sites()
    {
        var events = Source("Services/CompositorService.Events.cs");
        var evaluate = Method(events, "private void EvaluateSurfaceModOffThread(");
        Assert.Contains("ShellBodyAtr.Moved(", evaluate);
        Assert.Contains("_bodyAtrLatch.TryArm(", evaluate);
        Assert.Contains("DecideSettingChange(", evaluate);

        var onChange = Method(events, "private void OnModSettingChanged(");
        int window = onChange.IndexOf("ModSettingChange.TemporarySetting)", StringComparison.Ordinal);
        int watches = onChange.IndexOf("ShellBodyAtr.Watches(", window, StringComparison.Ordinal);
        int ret = onChange.IndexOf("return;", window, StringComparison.Ordinal);
        Assert.True(window >= 0 && watches > window && watches < ret, "the 1500 ms window returns before checking shapes");

        var read = Method(Source("Services/SecondSkinService.cs"), "internal ShellBodyAtr.ModSettings? PlayerModSettings(");
        Assert.Contains("RunOnFrameworkThread(", read);
        Assert.Contains("penumbra.GetModSettings(", read);
    }

    // ── AC-4: an unchanged or unreadable re-read is no change ─────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-4")]
    public void Own_uses_are_never_moved_under_unchanged_settings()
    {
        using var m = new Mods(Meta(Chests(), Nipples(), Vulva()));
        using var raced = new Mods(Meta(Chests(), Nipples("\"GenderRaceCondition\":201")));
        using var guarded = new Mods(Meta(Group("Chest", 0,
            Option("None", ChestAttrs.Select(c => Atr(c, false)).ToArray()),
            Option("Large", Atr("atrx_c_large", true)))));

        var cases = new (Mods Mod, Settings Settings, string Part, ushort? Race)[]
        {
            (m, new Settings(), "dwn", 201),                                        // "" — nothing off on the legs
            (m, new Settings(Pick("Chest", "Small Perky")), "top", 201),            // an Off set
            (m, new Settings(enabled: false), "top", 201),                          // "?" — disabled
            (guarded, new Settings(Pick("Chest", "None")), "top", 201),             // "?" — every chest off
            (m, new Settings { Unreadable = true }, "top", 201),                    // "?" — unreadable
            (raced, new Settings(new() { ["Chest"] = ["Flat"], ["Nipples"] = ["Inverted"] }), "top", null),   // race unknown
        };
        foreach (var (mod, settings, part, race) in cases)
        {
            var file = part == "top" ? mod.Top : mod.Dwn;
            var use = ShellBodyAtr.ForPart(part, true, file, mod.Root, race, settings.Of).Use!;
            Assert.False(ShellBodyAtr.Moved([use], null, settings.Of, out var why, out _), why);
        }

        // Both parts of one mod at once.
        var s = new Settings(Pick("Chest", "Flat"));
        var both = new[]
        {
            Top(m, s).Use!,
            ShellBodyAtr.ForPart("dwn", true, m.Dwn, m.Root, 201, s.Of).Use!,
        };
        Assert.False(ShellBodyAtr.Moved(both, null, s.Of, out _, out var target));
        Assert.Equal(ShellBodyAtr.Canonical(both), target);

        // Explicitly ticking the default reads the same as leaving the group out.
        var dflt = Top(m, new Settings()).Use!;
        Assert.False(ShellBodyAtr.Moved([dflt], null, new Settings(Pick("Chest", "Large")).Of, out _, out _));
    }

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-4")]
    public void Gate_skips_when_nothing_moved()
    {
        Assert.True(CompositorService.SkipUnchanged(false, false, "fp", "fp", () => false));
        Assert.False(CompositorService.SkipUnchanged(true, false, "fp", "fp", () => false));
        Assert.False(CompositorService.SkipUnchanged(false, true, "fp", "fp", () => false));
        Assert.False(CompositorService.SkipUnchanged(false, false, null, "fp", () => false));
    }

    // ── AC-5: every other body is untouched, at no cost ───────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-5")]
    public void A_body_without_switches_is_untouched()
    {
        // A body mod of files only, the way Rue, YAB or Neolithe are.
        using var m = new Mods(Meta("{\"Name\":\"Size\",\"Type\":\"Single\",\"Priority\":0,\"DefaultSettings\":0,"
                                    + "\"Options\":[{\"Name\":\"M\",\"Files\":{\"chara/x.mdl\":\"body/c0201e0000_top.mdl\"}}]}"));
        var s = new Settings();
        var atr = Top(m, s);
        Assert.Null(atr.Off);
        Assert.Null(atr.Use);
        Assert.Equal(0, s.Calls);

        var live = new HashSet<string>(StringComparer.Ordinal) { "atr_dv_a" };
        Assert.Same(live, ShellBodyAtr.Merge(live, null));
        Assert.Same(live, ShellBodyAtr.Merge(live, new HashSet<string>()));

        var body = ThreeChests("atr_dv_a");
        Assert.Equal(Shell(body, live), Shell(body, ShellBodyAtr.Merge(live, atr.Off)));
    }

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-5")]
    public void Empty_uses_cost_no_settings_calls()
    {
        var s = new Settings();
        Assert.False(ShellBodyAtr.Moved([], null, s.Of, out _, out _));
        Assert.False(ShellBodyAtr.Moved([], "NIRAE", s.Of, out _, out _));
        Assert.Equal(0, s.Calls);

        // The gate asks only when everything else says skip.
        Assert.False(CompositorService.SkipUnchanged(false, false, "a", "b", () => throw new InvalidOperationException()));
        Assert.False(CompositorService.SkipUnchanged(true, false, "a", "a", () => throw new InvalidOperationException()));

        var gate = Method(Source("Services/CompositorService.CompositeRun.cs"), "private bool GateUnchanged(");
        Assert.Contains("SkipUnchanged(", gate);
        Assert.Contains("compositor._lastBodyAtrUses", gate);
        Assert.Contains("compositor.secondSkin.PlayerModSettings", gate);

        var harvest = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void HarvestParts(");
        Assert.DoesNotContain("GetModDirectory(", harvest);
        Assert.Contains("ShellBodyAtr.ForPart(", harvest);
        Assert.Contains("isBarePart", Calls(harvest, "ShellBodyAtr.ForPart").Single());
        var surfaces = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void ResolveSurfaces(");
        Assert.Contains("ShellBodyAtr.HiddenFor(", surfaces);   // Merge of the live and option-switched hides (#58)
    }

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-5")]
    public void SwitchesShapes_is_memoised_by_fingerprint()
    {
        using var m = new Mods(Meta(Chests()));
        Assert.True(BodyAtrSwitches.SwitchesShapes(m.ModRoot));

        // Unreadable now, but the same length and write time: the answer comes from memory, not the file.
        var path = Path.Combine(m.ModRoot, "meta.json");
        var info = new FileInfo(path);
        long length = info.Length;
        var written = info.LastWriteTimeUtc;
        File.WriteAllText(path, new string(' ', (int)length));
        File.SetLastWriteTimeUtc(path, written);
        Assert.True(BodyAtrSwitches.SwitchesShapes(m.ModRoot));

        // An edit to the manifest is seen.
        m.Rewrite(Meta());
        Assert.False(BodyAtrSwitches.SwitchesShapes(m.ModRoot));
    }

    // ── AC-6: nothing readable, nothing hidden ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-6")]
    public void Unreadable_settings_hide_nothing()
    {
        using var m = new Mods(Meta(Chests()));
        var atr = Top(m, new Settings { Unreadable = true });
        Assert.Null(atr.Off);
        Assert.False(atr.Readable);
        Assert.Equal(ShellBodyAtr.NoSay, atr.Use!.Key);

        var covered = DrawnTags(Shell(ThreeChests(), ShellBodyAtr.Merge(null, atr.Off)));
        Assert.All(ChestAttrs, c => Assert.Contains(c, covered));
    }

    // ── AC-7: another collection, another chest ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-nirae-shell-chest-7")]
    public void Gate_composites_when_the_collection_draws_another_chest()
    {
        using var m = new Mods(Meta(Chests()));
        var uses = new[] { Top(m, new Settings()).Use! };
        var latch = new ShellBodyAtr.ForceLatch();
        var other = new Settings(Pick("Chest", "Small Perky"));
        Assert.False(CompositorService.SkipUnchanged(false, false, "fp", "fp",
            () => ShellBodyAtr.Moved(uses, null, other.Of, out _, out var t) && !latch.IsExhausted(t)));

        var gate = Method(Source("Services/CompositorService.CompositeRun.cs"), "private bool GateUnchanged(");
        Assert.Contains("IsExhausted(", gate);
    }

    // ── the edges ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-1")]
    public void Disabled_mod_hides_nothing()
    {
        using var m = new Mods(Meta(Chests()));
        var atr = Top(m, new Settings(Pick("Chest", "Flat"), enabled: false));
        Assert.Null(atr.Off);
        Assert.True(atr.Readable);
        Assert.Equal(ShellBodyAtr.NoSay, atr.Use!.Key);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-2")]
    public void Republished_body_is_judged_by_its_upstream_mod()
    {
        using var m = new Mods(Meta(Chests()));
        var s = new Settings(Pick("Chest", "Flat"));
        Assert.NotNull(Top(m, s).Off);

        // Our own output folder switches nothing, whatever file of it the path resolves to.
        var ours = Path.Combine(m.Root, SidecarDiscoveryService.ManagedModDir, "models", "c0201e0000_top.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(ours)!);
        File.WriteAllBytes(ours, new byte[100]);
        var calls = s.Calls;
        Assert.Null(ShellBodyAtr.ForPart("top", true, ours, m.Root, 201, s.Of).Use);
        Assert.Equal(calls, s.Calls);

        var harvest = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void HarvestParts(");
        Assert.Contains("ShellBodyAtr.ReadSource(", harvest);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-3")]
    public void Unknown_or_outside_file_never_asks_penumbra()
    {
        using var m = new Mods(Meta(Chests()));
        var s = new Settings();
        Assert.Null(ShellBodyAtr.ForPart("top", true, null, m.Root, 201, s.Of).Use);
        Assert.Null(ShellBodyAtr.ForPart("top", true, "chara/equipment/e0000/model/c0201e0000_top.mdl", m.Root, 201, s.Of).Use);
        Assert.Null(ShellBodyAtr.ForPart("top", true, Path.Combine(Path.GetTempPath(), "elsewhere", "top.mdl"), m.Root, 201, s.Of).Use);
        Assert.Null(ShellBodyAtr.ForPart("top", true, m.Top, "", 201, s.Of).Use);
        Assert.Null(ShellBodyAtr.ForPart("top", true, m.Top, null, 201, s.Of).Use);
        Assert.Equal(0, s.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-4")]
    public void A_garment_part_is_not_consulted()
    {
        using var m = new Mods(Meta(Chests()));
        var s = new Settings(Pick("Chest", "Flat"));
        var atr = Top(m, s, bare: false);
        Assert.Null(atr.Off);
        Assert.Null(atr.Use);
        Assert.Equal(0, s.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-5")]
    public void Unknown_race_leaves_race_limited_entries_drawn()
    {
        // The chests switch for every race; the nipples only for c0201.
        using var m = new Mods(Meta(Chests(), Nipples("\"GenderRaceCondition\":201")));
        var s = new Settings(new() { ["Chest"] = ["Flat"], ["Nipples"] = ["Inverted"] });
        Assert.Contains("atrx_n_soft", Top(m, s, race: 201).Off!);
        var unknown = Top(m, s, race: null).Off!;
        Assert.DoesNotContain(unknown, a => a.StartsWith("atrx_n_", StringComparison.Ordinal));
        Assert.Contains("atrx_c_large", unknown);

        var harvest = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void HarvestParts(");
        Assert.Contains("RefitCore.RaceNumber(drawnRaceCode)", harvest);
        Assert.DoesNotContain("drawnRaceCode ?? charCode", harvest);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-6")]
    public void A_stale_option_name_hides_nothing_of_its_group()
    {
        using var m = new Mods(Meta(Chests()));
        var atr = Top(m, new Settings(Pick("Chest", "Gone")));
        Assert.True(atr.Off == null || !atr.Off.Any(a => a.StartsWith("atrx_c_", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-7")]
    public void Live_variant_and_option_hides_are_unioned()
    {
        var live = new HashSet<string>(StringComparer.Ordinal) { "atr_dv_a" };
        var merged = ShellBodyAtr.Merge(live, new HashSet<string>(StringComparer.Ordinal) { "atrx_c_flat" })!;
        Assert.Equal(["atr_dv_a", "atrx_c_flat"], merged.Order(StringComparer.Ordinal));
        Assert.Single(live);   // the live set itself is not changed

        var covered = DrawnTags(Shell(ThreeChests("atr_dv_a"), merged));
        Assert.DoesNotContain("atrx_c_flat", covered);
        Assert.Equal(2, covered.Count(t => t.StartsWith("atrx_c_", StringComparison.Ordinal)));
        Assert.DoesNotContain("atr_dv_a", covered);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-8")]
    public void Each_part_is_judged_by_its_own_slot()
    {
        using var m = new Mods(Meta(Chests(), Vulva()));
        var s = new Settings();
        var uses = new[] { Top(m, s).Use!, ShellBodyAtr.ForPart("dwn", true, m.Dwn, m.Root, 201, s.Of).Use! };
        Assert.True(ShellBodyAtr.Moved(uses, null, new Settings(Pick("Chest", "Flat")).Of, out var why, out _));
        Assert.Contains("top:", why);
        Assert.DoesNotContain("dwn: [", why);
        Assert.False(ShellBodyAtr.Moved(uses, null, new Settings().Of, out _, out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-9")]
    public void No_recorded_use_is_never_moved()
    {
        var s = new Settings(Pick("Chest", "Flat"));
        Assert.False(ShellBodyAtr.Moved([], "NIRAE", s.Of, out _, out _));
        var other = new ShellBodyAtr.Use("Other", "x", "top", 201, "");
        Assert.False(ShellBodyAtr.Moved([other], "NIRAE", s.Of, out _, out _));
        Assert.Equal(0, s.Calls);
        Assert.False(ShellBodyAtr.Watches([other], "NIRAE"));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-11")]
    public void A_throwing_settings_read_is_not_moved()
    {
        using var m = new Mods(Meta(Chests()));
        var use = Top(m, new Settings()).Use!;
        Assert.False(ShellBodyAtr.Moved([use], null, new Settings { Throws = true }.Of, out _, out var target));
        Assert.Equal(ShellBodyAtr.Canonical([use]), target);
        Assert.False(ShellBodyAtr.Moved([use], null, _ => throw new InvalidOperationException(), out _, out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-12")]
    public void Latch_ignores_publishes_from_runs_started_before_the_arm()
    {
        var latch = new ShellBodyAtr.ForceLatch();
        int asked = 0, warned = 0;
        long earlier = latch.Stamp();
        Assert.True(latch.TryArm("t"));
        latch.OnPublished("old", earlier, () => { asked++; return "t"; }, _ => warned++);
        Assert.Equal(0, asked);
        Assert.False(latch.IsExhausted("t"));
        Assert.False(latch.TryArm("t"));   // still pending

        latch.OnPublished("t", latch.Stamp(), () => { asked++; return "t"; }, _ => warned++);
        Assert.True(latch.TryArm("t"));    // cleared by the publish that drew it
        Assert.Equal(0, warned);

        var run = Source("Services/CompositorService.CompositeRun.cs");
        var publish = Method(run, "private void Publish(");
        int fp = publish.IndexOf("compositor._lastCompositeFingerprint = fingerprint", StringComparison.Ordinal);
        int force = publish.IndexOf("TryForceForShapes(null", StringComparison.Ordinal);
        Assert.True(fp >= 0 && force > fp, "the post-publish re-check must follow the fingerprint");
        Assert.Contains("_bodyAtrLatch.OnPublished(", Method(run, "private bool CommitLocators("));
        var begin = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private bool Begin(");
        Assert.DoesNotContain("BodyAtrUses", begin);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-13")]
    public void Restart_reads_the_upstream_source_sidecar()
    {
        using var m = new Mods(Meta(Chests()));
        var upstream = Path.Combine(m.Root, "out", "upstream", "top.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(upstream)!);
        File.WriteAllBytes(upstream, new byte[100]);

        ShellBodyAtr.WriteSource(upstream, m.Top, m.Root);
        var stored = File.ReadAllText(upstream + ".src");
        Assert.DoesNotContain(m.Root, stored);
        Assert.Equal("NIRAE/body/c0201e0000_top.mdl", stored);

        // A fresh string for the same root, as after a restart.
        var root = new string(m.Root.ToCharArray());
        Assert.Equal(Path.GetFullPath(m.Top), Path.GetFullPath(ShellBodyAtr.ReadSource(upstream, root, 100)!));
        File.Delete(m.Top);
        Assert.Null(ShellBodyAtr.ReadSource(upstream, root, 100));

        var harvest = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void HarvestParts(");
        Assert.Contains("ShellBodyAtr.ReadSource(upstreamDisk, modsRoot, bytes.Length)", harvest);
        // Bare parts only: a garment's restored source would change what smoothing republishes.
        Assert.Matches(@"if \(isBarePart\)\s*\{\s*sourceDisk = ShellBodyAtr\.ReadSource\(", harvest);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-14")]
    public void Hiding_every_chest_fails_open()
    {
        using var m = new Mods(Meta(Group("Chest", 0,
            Option("None", ChestAttrs.Select(c => Atr(c, false)).ToArray()),
            Option("Large", Atr("atrx_c_large", true)))));
        var atr = Top(m, new Settings(Pick("Chest", "None")));
        Assert.Null(atr.Off);
        Assert.True(atr.Readable);
        Assert.Equal(ShellBodyAtr.NoSay, atr.Use!.Key);
        Assert.True(atr.Warning);
        Assert.NotNull(atr.Note);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-15")]
    public void An_empty_off_part_is_watched()
    {
        // Under its default the legs draw both, so nothing is off on them.
        using var empty = new Mods(Meta(Chests(), Group("Vulva", 0,
            Option("Both", Atr("atrx_v_closed", true, "Legs"), Atr("atrx_v_open", true, "Legs")),
            Option("Open", Atr("atrx_v_closed", false, "Legs"), Atr("atrx_v_open", true, "Legs")))));
        var use = ShellBodyAtr.ForPart("dwn", true, empty.Dwn, empty.Root, 201, new Settings().Of).Use!;
        Assert.Equal("", use.Key);
        Assert.True(ShellBodyAtr.Moved([use], null, new Settings(Pick("Vulva", "Open")).Of, out _, out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-16")]
    public void Window_check_is_counted_as_evaluating()
    {
        var use = new ShellBodyAtr.Use("NIRAE", "x", "top", 201, "");
        Assert.True(ShellBodyAtr.Watches([use], "nirae"));
        Assert.False(ShellBodyAtr.Watches([use], "Rue"));

        var check = Method(Source("Services/CompositorService.Events.cs"), "private void CheckBodyShapesOffThread(");
        int plus = check.IndexOf("NoteEvaluating(modDir, +1)", StringComparison.Ordinal);
        int task = check.IndexOf("Task.Run(", StringComparison.Ordinal);
        int force = check.IndexOf("TryForceForShapes(", StringComparison.Ordinal);
        int fin = check.IndexOf("finally", StringComparison.Ordinal);
        int minus = check.IndexOf("NoteEvaluating(modDir, -1)", StringComparison.Ordinal);
        Assert.True(plus >= 0 && plus < task && task < force && force < fin && fin < minus);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-17")]
    public void Every_layer_on_the_body_skips_hidden_chests()
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal) { "atrx_c_flat", "atrx_c_large" };
        var built = Shell(ThreeChests(), hidden, layers: 2);
        var parts = ModelPartReader.Read(built)!;
        var materials = parts.Parts.Where(p => p.TriangleCount > 0).Select(p => p.Material).Distinct().ToList();
        Assert.Equal(2, materials.Count);
        foreach (var mat in materials)
        {
            var tags = parts.Parts.Where(p => p.Material == mat && p.TriangleCount > 0)
                .SelectMany(p => Enumerable.Range(0, parts.AttributeNames.Count)
                    .Where(b => (p.AttributeMask & (1u << b)) != 0).Select(b => parts.AttributeNames[b])).ToList();
            Assert.Contains("atrx_c_perky", tags);
            Assert.DoesNotContain("atrx_c_flat", tags);
            Assert.DoesNotContain("atrx_c_large", tags);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-18")]
    public void Latch_forces_once_per_target_then_stops()
    {
        var latch = new ShellBodyAtr.ForceLatch();
        var warnings = new List<string>();
        Assert.True(latch.TryArm("t"));
        Assert.False(latch.TryArm("t"));

        // The build drew something else while the settings still say t: they disagree. Once.
        latch.OnPublished("p", latch.Stamp(), () => "t", warnings.Add);
        Assert.Single(warnings);
        Assert.True(latch.IsExhausted("t"));
        Assert.False(latch.TryArm("t"));
        Assert.True(latch.TryArm("u"));
        latch.OnPublished("u", latch.Stamp(), () => throw new InvalidOperationException("not asked"), warnings.Add);
        Assert.Single(warnings);
        // The shell followed a forced change away from t (a Glamourer design, say): going back to t is a new change.
        Assert.False(latch.IsExhausted("t"));
        Assert.True(latch.TryArm("t"));
        latch.OnPublished("p", latch.Stamp(), () => "t", warnings.Add);
        Assert.Equal(2, warnings.Count);
        Assert.True(latch.IsExhausted("t"));
        warnings.RemoveAt(1);

        // A publish that does draw t after all: it may be forced again, from any run, armed or not.
        latch.OnPublished("t", 0, () => "t", warnings.Add);
        Assert.False(latch.IsExhausted("t"));
        Assert.True(latch.TryArm("t"));
        Assert.Single(warnings);

        // A deliberate settings change forgives: what was given up on may be forced again.
        latch.OnPublished("p", latch.Stamp(), () => "t", warnings.Add);
        Assert.True(latch.IsExhausted("t"));
        latch.ClearExhausted();
        Assert.False(latch.IsExhausted("t"));
        Assert.True(latch.TryArm("t"));
        var evaluate = Method(Source("Services/CompositorService.Events.cs"), "private void EvaluateSurfaceModOffThread(");
        Assert.Contains("if (force && ShellBodyAtr.Watches(_lastBodyAtrUses, modDir)) _bodyAtrLatch.ClearExhausted();", evaluate);
        warnings.Clear();
        warnings.Add("(the forgiven one)");

        // A fresh read that fails is no disagreement.
        latch.OnPublished("p", latch.Stamp(), () => throw new InvalidOperationException(), warnings.Add);
        Assert.Single(warnings);
        Assert.False(latch.IsExhausted("t"));
        Assert.True(latch.TryArm("t"));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-19")]
    public void Unreadable_now_never_counts_as_moved()
    {
        using var m = new Mods(Meta(Chests()));
        var readable = Top(m, new Settings()).Use!;
        Assert.False(ShellBodyAtr.Moved([readable], null, new Settings { Unreadable = true }.Of, out _, out var t1));
        Assert.Equal(ShellBodyAtr.Canonical([readable]), t1);
        Assert.False(ShellBodyAtr.Moved([readable], null, new Settings { Throws = true }.Of, out _, out _));

        var unread = Top(m, new Settings { Unreadable = true }).Use!;
        Assert.True(ShellBodyAtr.Moved([unread], null, new Settings(Pick("Chest", "Flat")).Of, out _, out _));

        var disabled = Top(m, new Settings(enabled: false)).Use!;
        Assert.False(ShellBodyAtr.Moved([disabled], null, new Settings(enabled: false).Of, out _, out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-20")]
    public void Sidecar_never_points_at_another_body()
    {
        using var m = new Mods(Meta(Chests()));
        var upstream = Path.Combine(m.Root, "out", "upstream", "top.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(upstream)!);

        ShellBodyAtr.WriteSource(upstream, m.Top, m.Root);
        Assert.True(File.Exists(upstream + ".src"));
        ShellBodyAtr.WriteSource(upstream, null, m.Root);                   // rewritten from game data
        Assert.False(File.Exists(upstream + ".src"));

        ShellBodyAtr.WriteSource(upstream, m.Top, m.Root);
        ShellBodyAtr.WriteSource(upstream, Path.Combine(Path.GetTempPath(), "x.mdl"), m.Root);   // outside the root
        Assert.False(File.Exists(upstream + ".src"));

        ShellBodyAtr.WriteSource(upstream, m.Top, m.Root);
        Assert.Null(ShellBodyAtr.ReadSource(upstream, m.Root, 99));      // the copy is not that file any more
        Assert.NotNull(ShellBodyAtr.ReadSource(upstream, m.Root, 100));

        // A record that leaves the mods folder, or says nothing, is no record — whatever file it would reach.
        File.WriteAllBytes(Path.Combine(m.Root, "escaped.mdl"), new byte[100]);
        foreach (var record in new[] { "NIRAE/../escaped.mdl", "../" + Path.GetFileName(m.Root) + "/escaped.mdl",
                                       m.Top, "   ", "" })
        {
            File.WriteAllText(upstream + ".src", record);
            Assert.Null(ShellBodyAtr.ReadSource(upstream, m.Root, 100));
        }

        // The record follows the copy: right after each write of it, in the same try.
        var harvest = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void HarvestParts(");
        var writes = Regex.Matches(harvest, @"WriteIfChanged\(upstreamDisk[^;]*;\s*ShellBodyAtr\.WriteSource\(upstreamDisk");
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-22")]
    public void Latch_quietly_releases_when_settings_moved_again()
    {
        using var m = new Mods(Meta(Chests()));
        var latch = new ShellBodyAtr.ForceLatch();
        var warnings = new List<string>();

        // Recorded under Large; Flat armed; the user picks Small Perky before the build reads: Small Perky published.
        var atC = new[] { Top(m, new Settings(Pick("Chest", "Small Perky"))).Use! };
        var flat = Top(m, new Settings(Pick("Chest", "Flat"))).Use!;
        string b = ShellBodyAtr.Canonical([flat]), c = ShellBodyAtr.Canonical(atC);
        Assert.True(latch.TryArm(b));
        latch.OnPublished(c, latch.Stamp(), () => c, warnings.Add);
        Assert.Empty(warnings);
        Assert.False(latch.IsExhausted(b));

        // ...and goes back to Flat: still forced.
        Assert.True(ShellBodyAtr.Moved(atC, null, new Settings(Pick("Chest", "Flat")).Of, out _, out var target));
        Assert.Equal(b, target);
        Assert.True(latch.TryArm(b));

        // Unreadable at publish time: the same quiet release.
        latch.OnPublished(c, latch.Stamp(), () => null, warnings.Add);
        Assert.Empty(warnings);
        Assert.False(latch.IsExhausted(b));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-23")]
    public void Latch_releases_pending_when_a_forced_run_does_not_publish()
    {
        var latch = new ShellBodyAtr.ForceLatch();
        var warnings = new List<string>();
        long s0 = latch.Stamp();
        Assert.True(latch.TryArm("t"));
        latch.OnRunEnded(s0, published: false);       // started before the arm
        Assert.False(latch.TryArm("t"));

        long s1 = latch.Stamp();
        latch.OnRunEnded(s1, published: true);        // a publish says nothing here
        Assert.False(latch.TryArm("t"));
        latch.OnRunEnded(s1, published: false);
        Assert.False(latch.IsExhausted("t"));

        // Released, but a later publish is still judged against it.
        latch.OnPublished("p", latch.Stamp(), () => "t", warnings.Add);
        Assert.Single(warnings);
        Assert.True(latch.IsExhausted("t"));

        // Released, and armed again.
        var again = new ShellBodyAtr.ForceLatch();
        Assert.True(again.TryArm("u"));
        again.OnRunEnded(again.Stamp(), published: false);
        Assert.True(again.TryArm("u"));
    }

    [Fact]
    [Trait("Plan", "EDGE-nirae-shell-chest-18")]
    [Trait("Plan", "EDGE-nirae-shell-chest-23")]
    public void Run_releases_and_confirms_through_the_latch()
    {
        var run = Source("Services/CompositorService.CompositeRun.cs");
        var body = Method(run, "public void Run(");
        int fin = body.LastIndexOf("finally", StringComparison.Ordinal);
        Assert.True(fin >= 0 && body.IndexOf("_bodyAtrLatch.OnRunEnded(latchStamp, publishedThisRun", fin, StringComparison.Ordinal) > fin);

        var commit = Method(run, "private bool CommitLocators(");
        var published = Calls(commit, "_bodyAtrLatch.OnPublished").Single();
        Assert.Contains("ShellBodyAtr.Moved(", published);
        Assert.Contains("PlayerModSettings", published);
        Assert.True(commit.IndexOf("publishedThisRun = true", StringComparison.Ordinal)
                    > commit.IndexOf("_bodyAtrLatch.OnPublished(", StringComparison.Ordinal));
    }

    // ── #58: one hidden set for the shell's source and the nail flatten ──────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-5")]
    [Trait("Plan", "EDGE-issue-sweep-a-14")]
    [Trait("Plan", "EDGE-issue-sweep-a-15")]
    public void HiddenFor_merges_live_and_option_switched_hides()
    {
        const string glv = "chara/equipment/e0000/model/c0201e0000_glv.mdl";
        var none = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<string> { "shp_a", Interop.BodyShapeReader.HiddenAttributePrefix + "atr_gv_c" };
        var liveHidden = Interop.BodyShapeReader.Split(live).HiddenAttributes;
        // Live only: exactly the live set; neither: null.
        Assert.Equal(liveHidden, ShellBodyAtr.HiddenFor(live, none, glv));
        Assert.Null(ShellBodyAtr.HiddenFor(null, none, glv));
        // Option-switched only, found through the dictionary's comparer (another case).
        var off = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [glv.ToUpperInvariant()] = new HashSet<string> { "atr_gv_d" },
        };
        Assert.Equal(["atr_gv_d"], ShellBodyAtr.HiddenFor(null, off, glv)!);
        // Both: the union.
        var both = ShellBodyAtr.HiddenFor(live, off, glv)!;
        Assert.Equal((liveHidden ?? []).Append("atr_gv_d").Order(), both.Order());
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-5")]
    public void Shell_build_gives_flatten_nails_the_merged_hides()
    {
        var src = Source("Services/SecondSkinService.ShellSetBuild.cs");
        var flatten = Assert.Single(Calls(src, "BodyBridge.FlattenNails"));
        Assert.Contains("ShellBodyAtr.HiddenFor(", flatten);
        Assert.Matches(@"HiddenAttributes:\s*ShellBodyAtr\.HiddenFor\(", src);
    }

    // ── reading the source, as AutoRedrawWiringTests does ─────────────────────────────────────────────────

    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    private static string[] Calls(string body, string callee)
        => Regex.Matches(body, Regex.Escape(callee) + @"\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)")
                .Select(m => m.Value).ToArray();
}
