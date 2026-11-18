using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Proteus's public IPC, tested from <c>docs/contrib-clean-room-spec.md</c> §2, §3 and §9 only. Rows the old code can be
/// reached by without a new seam (R1, R5/R6 guard paths, R7, R8, R10 for R1) are gated on the old code; the rest go
/// through the entry-list seam B adds (spec §9.1 says the old code has none) and carry CleanRoom=NewSeam.
/// </summary>
public class IpcProviderSpecTests
{
    private const string Plan1 = "AC-contrib-clean-room-1";

    // The overlay-list element: (ModDirectory, Name, Priority, Options), spec §2.
    private static readonly string[] Labels =
        ["Proteus.ApiVersion", "Proteus.GetOverlays", "Proteus.GetActiveOverlays", "Proteus.Recomposite",
         "Proteus.GetColorTable", "Proteus.SetColorTable"];

    private sealed class Gates
    {
        public readonly IDalamudPluginInterface Pi = Substitute.For<IDalamudPluginInterface>();
        public readonly ICallGateProvider<(int, int)> Version = Substitute.For<ICallGateProvider<(int, int)>>();
        public readonly ICallGateProvider<List<(string, string, int, Dictionary<string, List<string>>?)>> Overlays =
            Substitute.For<ICallGateProvider<List<(string, string, int, Dictionary<string, List<string>>?)>>>();
        public readonly ICallGateProvider<List<(string, string, int, Dictionary<string, List<string>>?)>> Active =
            Substitute.For<ICallGateProvider<List<(string, string, int, Dictionary<string, List<string>>?)>>>();
        public readonly ICallGateProvider<object> Recomposite = Substitute.For<ICallGateProvider<object>>();
        public readonly ICallGateProvider<string, string?, string?, string> GetTable =
            Substitute.For<ICallGateProvider<string, string?, string?, string>>();
        public readonly ICallGateProvider<string, string?, string?, string, bool> SetTable =
            Substitute.For<ICallGateProvider<string, string?, string?, string, bool>>();

        public Func<(int, int)>? VersionFunc;
        public Func<List<(string, string, int, Dictionary<string, List<string>>?)>>? OverlaysFunc, ActiveFunc;
        public Action? RecompositeAction;
        public Func<string, string?, string?, string>? GetTableFunc;
        public Func<string, string?, string?, string, bool>? SetTableFunc;

        public Gates()
        {
            Pi.GetIpcProvider<(int, int)>("Proteus.ApiVersion").Returns(Version);
            Pi.GetIpcProvider<List<(string, string, int, Dictionary<string, List<string>>?)>>("Proteus.GetOverlays").Returns(Overlays);
            Pi.GetIpcProvider<List<(string, string, int, Dictionary<string, List<string>>?)>>("Proteus.GetActiveOverlays").Returns(Active);
            Pi.GetIpcProvider<object>("Proteus.Recomposite").Returns(Recomposite);
            Pi.GetIpcProvider<string, string?, string?, string>("Proteus.GetColorTable").Returns(GetTable);
            Pi.GetIpcProvider<string, string?, string?, string, bool>("Proteus.SetColorTable").Returns(SetTable);
            Version.RegisterFunc(Arg.Do<Func<(int, int)>>(f => VersionFunc = f));
            Overlays.RegisterFunc(Arg.Do<Func<List<(string, string, int, Dictionary<string, List<string>>?)>>>(f => OverlaysFunc = f));
            Active.RegisterFunc(Arg.Do<Func<List<(string, string, int, Dictionary<string, List<string>>?)>>>(f => ActiveFunc = f));
            Recomposite.RegisterAction(Arg.Do<Action>(a => RecompositeAction = a));
            GetTable.RegisterFunc(Arg.Do<Func<string, string?, string?, string>>(f => GetTableFunc = f));
            SetTable.RegisterFunc(Arg.Do<Func<string, string?, string?, string, bool>>(f => SetTableFunc = f));
        }
    }

    private static Configuration Config(bool enabled) => new() { PluginEnabled = enabled };

    // ── Gated on the old code (no new seam) ───────────────────────────────────

    [Fact]
    [Trait("Plan", Plan1)]
    public void Registers_exactly_the_documented_labels_and_signatures()   // [AC-contrib-clean-room-1]
    {
        var g = new Gates();
        _ = new IpcProvider(g.Pi, null!, null!, Config(true), Substitute.For<IPluginLog>());

        // R7: one provider per label, registered at once, in the documented order; nothing else asked of Dalamud.
        Received.InOrder(() =>
        {
            g.Version.RegisterFunc(Arg.Any<Func<(int, int)>>());
            g.Overlays.RegisterFunc(Arg.Any<Func<List<(string, string, int, Dictionary<string, List<string>>?)>>>());
            g.Active.RegisterFunc(Arg.Any<Func<List<(string, string, int, Dictionary<string, List<string>>?)>>>());
            g.Recomposite.RegisterAction(Arg.Any<Action>());
            g.GetTable.RegisterFunc(Arg.Any<Func<string, string?, string?, string>>());
            g.SetTable.RegisterFunc(Arg.Any<Func<string, string?, string?, string, bool>>());
        });
        var piCalls = g.Pi.ReceivedCalls().ToList();
        Assert.All(piCalls, c => Assert.Equal("GetIpcProvider", c.GetMethodInfo().Name));
        Assert.Equal(Labels, piCalls.Select(c => (string)c.GetArguments()[0]!).ToArray());
    }

    [Fact]
    [Trait("Plan", Plan1)]
    public void ApiVersion_is_one_zero()   // [AC-contrib-clean-room-1]
    {
        var g = new Gates();
        _ = new IpcProvider(g.Pi, null!, null!, Config(true), Substitute.For<IPluginLog>());
        Assert.Equal((1, 0), g.VersionFunc!());
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("Plan", "EDGE-contrib-clean-room-8")]
    public void Answers_on_the_thread_the_spec_says()   // [AC-contrib-clean-room-1] [EDGE-contrib-clean-room-8]
    {
        // R10: no marshalling; the answer comes back on the caller's (non-framework) thread.
        var g = new Gates();
        _ = new IpcProvider(g.Pi, null!, null!, Config(true), Substitute.For<IPluginLog>());
        (int, int) got = default;
        Exception? error = null;
        var t = new Thread(() => { try { got = g.VersionFunc!(); } catch (Exception e) { error = e; } });
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(error);
        Assert.Equal((1, 0), got);
    }

    [Fact]
    [Trait("Plan", Plan1)]
    public void Dispose_unregisters_every_label()   // [AC-contrib-clean-room-1]
    {
        var g = new Gates();
        var p = new IpcProvider(g.Pi, null!, null!, Config(true), Substitute.For<IPluginLog>());
        p.Dispose();
        Received.InOrder(() =>   // R8 order
        {
            g.Version.UnregisterFunc();
            g.Overlays.UnregisterFunc();
            g.Active.UnregisterFunc();
            g.Recomposite.UnregisterAction();
            g.GetTable.UnregisterFunc();
            g.SetTable.UnregisterFunc();
        });
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("Plan", "EDGE-contrib-clean-room-7")]
    public void GetColorTable_contains_any_failure_as_an_empty_string_and_one_error()   // [AC-contrib-clean-room-1] [EDGE-contrib-clean-room-7]
    {
        // R5/S8: with no compositor the lookup fails inside the guard.
        var g = new Gates();
        var log = Substitute.For<IPluginLog>();
        _ = new IpcProvider(g.Pi, null!, null!, Config(true), log);
        Assert.Equal(string.Empty, g.GetTableFunc!("mod", null, null));
        Assert.Single(log.ReceivedCalls(), c => c.GetMethodInfo().Name == "Error" && c.GetArguments().OfType<Exception>().Any());
    }

    [Fact]
    [Trait("Plan", Plan1)]
    public void SetColorTable_refuses_when_the_master_switch_is_off_before_any_lookup()   // [AC-contrib-clean-room-1]
    {
        // R6/S3: false at once; nothing logged, nothing touched (the compositor is null).
        var g = new Gates();
        var log = Substitute.For<IPluginLog>();
        _ = new IpcProvider(g.Pi, null!, null!, Config(false), log);
        Assert.False(g.SetTableFunc!("mod", null, null, "[]"));
        Assert.Empty(log.ReceivedCalls());
    }

    [Fact]
    [Trait("Plan", Plan1)]
    public void SetColorTable_contains_any_failure_as_false_and_one_error()   // [AC-contrib-clean-room-1]
    {
        var g = new Gates();
        var log = Substitute.For<IPluginLog>();
        _ = new IpcProvider(g.Pi, null!, null!, Config(true), log);
        Assert.False(g.SetTableFunc!("mod", null, null, "[]"));
        Assert.Single(log.ReceivedCalls(), c => c.GetMethodInfo().Name == "Error" && c.GetArguments().OfType<Exception>().Any());
    }

    // ── Through the entry-list seam B adds (spec §9.1: the old code has none) ──
    //
    // Seam contract (logged in the Contract's seam list): an internal constructor
    //   IpcProvider(IDalamudPluginInterface pluginInterface, Func<List<OverlayEntry>> lastDiscovered,
    //               Func<OverlayEntry, List<ResolvedOverlay>> resolveActiveOverlays, Func<OverlayEntry, bool> saveMetadata,
    //               Action<string> triggerRecomposite, Configuration config, IPluginLog log)
    // which the public constructor uses with the compositor's and discovery service's members (§9.2).

    private sealed class Seam
    {
        public readonly Gates G = new();
        public readonly IPluginLog Log = Substitute.For<IPluginLog>();
        public List<OverlayEntry> Entries = [];
        public readonly List<string> Recomposites = [];
        public readonly List<OverlayEntry> Saved = [];
        public Func<OverlayEntry, List<ResolvedOverlay>> Resolve = _ => [];

        public Seam(bool enabled = true) =>
            Provider = (IpcProvider)Activator.CreateInstance(typeof(IpcProvider),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                [G.Pi, (Func<List<OverlayEntry>>)(() => Entries), (Func<OverlayEntry, List<ResolvedOverlay>>)(e => Resolve(e)),
                 (Func<OverlayEntry, bool>)(e => { Saved.Add(e); return true; }), (Action<string>)Recomposites.Add,
                 Config(enabled), Log], null)!;

        public IpcProvider Provider { get; }
    }

    private static OverlayEntry Entry(string dir, bool enabled, ProteusMetadata meta, int priority = 5) =>
        new(dir, dir + " name", priority, enabled, meta, "sidecar");

    private static ProteusMetadata Grouped() => new()
    {
        ColorTableRows = [new ColorTableRowPreset { Row = 1 }],
        OptionGroups =
        [
            new OverlayOptionGroup
            {
                PenumbraGroupName = "Pattern",
                Options =
                [
                    new OverlayOption { Name = "Stripes", ColorTableRows = [new ColorTableRowPreset { Row = 7 }] },
                    new OverlayOption { Name = "Dots" },
                ],
            },
            new OverlayOptionGroup { PenumbraGroupName = "Gloves", Options = [new OverlayOption { Name = "Long" }] },
        ],
    };

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.1.R2")]
    public void GetOverlays_lists_enabled_entries_with_every_option_name()   // [AC-contrib-clean-room-1]
    {
        var s = new Seam();
        s.Entries = [Entry("a", true, Grouped(), 3), Entry("off", false, Grouped()), Entry("flat", true, new ProteusMetadata())];
        var list = s.G.OverlaysFunc!();
        Assert.Equal(["a", "flat"], list.Select(x => x.Item1));
        Assert.Equal(("a", "a name", 3), (list[0].Item1, list[0].Item2, list[0].Item3));
        Assert.Equal(["Pattern", "Gloves"], list[0].Item4!.Keys);
        Assert.Equal(["Stripes", "Dots"], list[0].Item4!["Pattern"]);
        Assert.False(list[0].Item4!.ContainsKey("pattern"));   // R11: returned keys are case-sensitive
        Assert.Null(list[1].Item4);                              // no option groups: null
        Assert.NotSame(list, s.G.OverlaysFunc!());               // a fresh list per call
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.1.R3")]
    public void GetActiveOverlays_groups_resolved_overlays_once_per_descriptor()   // [AC-contrib-clean-room-1]
    {
        var s = new Seam();
        var d = new OverlayDescriptor();
        s.Entries = [Entry("a", true, Grouped()), Entry("none", true, Grouped()), Entry("flat", true, new ProteusMetadata())];
        s.Resolve = e => e.ModDirectory switch
        {
            "a" =>
            [
                new ResolvedOverlay(d, null, "Pattern", "Stripes"), new ResolvedOverlay(d, null, "Pattern", "Stripes"),
                new ResolvedOverlay(d, null, "Gloves", "Long"), new ResolvedOverlay(d, null, null, null),
            ],
            _ => [],
        };
        var list = s.G.ActiveFunc!();
        Assert.Equal(["Pattern", "Gloves", ""], list[0].Item4!.Keys);
        Assert.Equal(["Stripes", "Stripes"], list[0].Item4!["Pattern"]);
        Assert.Equal([""], list[0].Item4![""]);
        Assert.NotNull(list[1].Item4);          // option groups but nothing selected: empty, not null
        Assert.Empty(list[1].Item4!);
        Assert.Null(list[2].Item4);             // no option groups: null
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.1.R4")]
    public void Recomposite_triggers_once_with_its_own_reason()   // [AC-contrib-clean-room-1]
    {
        var s = new Seam(enabled: false);   // R4 does not check the master switch
        s.G.RecompositeAction!();
        Assert.Single(s.Recomposites);
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.1.R5")]
    [Trait("Plan", "EDGE-contrib-clean-room-7")]
    public void GetColorTable_finds_entries_groups_and_options_case_insensitively()   // [AC-contrib-clean-room-1] [EDGE-contrib-clean-room-7]
    {
        var s = new Seam(enabled: false);   // R5 does not check the master switch, nor the enabled flag
        s.Entries = [Entry("Mod A", false, Grouped())];
        string Json(List<ColorTableRowPreset> rows) => JsonSerializer.Serialize(rows);
        var meta = s.Entries[0].Metadata;
        Assert.Equal(Json(meta.OptionGroups![0].Options[0].ColorTableRows!), s.G.GetTableFunc!("mod a", "PATTERN", "stripes"));
        Assert.Equal(Json(meta.ColorTableRows!), s.G.GetTableFunc!("MOD A", null, "ignored"));
        Assert.Equal("", s.G.GetTableFunc!("Mod A", "Pattern", "Dots"));    // option rows null: no fallback
        Assert.Equal("", s.G.GetTableFunc!("Mod A", "Pattern", null));      // group without option
        Assert.Equal("", s.G.GetTableFunc!("Mod A", "Nope", "Stripes"));
        Assert.Equal("", s.G.GetTableFunc!("Mod B", null, null));
        Assert.Equal("", s.G.GetTableFunc!(null!, null, null));
        Assert.DoesNotContain(s.Log.ReceivedCalls(), c => c.GetMethodInfo().Name == "Error");
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.1.R6")]
    public void SetColorTable_replaces_saves_recomposites_and_returns_true()   // [AC-contrib-clean-room-1]
    {
        var s = new Seam();
        s.Entries = [Entry("Mod A", true, Grouped())];
        var meta = s.Entries[0].Metadata;
        var rows = new List<ColorTableRowPreset> { new() { Row = 12 } };

        Assert.True(s.G.SetTableFunc!("mod a", "pattern", "DOTS", JsonSerializer.Serialize(rows)));
        Assert.Equal(12, Assert.Single(meta.OptionGroups![0].Options[1].ColorTableRows!).Row);
        Assert.Same(s.Entries[0], Assert.Single(s.Saved));
        Assert.Single(s.Recomposites);

        s.G.RecompositeAction!();   // R4's reason tag differs from R6's
        Assert.NotEqual(s.Recomposites[0], s.Recomposites[1]);

        Assert.True(s.G.SetTableFunc!("Mod A", null, "ignored", "   "));   // whitespace clears the top-level table
        Assert.Null(meta.ColorTableRows);
        Assert.True(s.G.SetTableFunc!("Mod A", "Pattern", "Stripes", "null"));
        Assert.Null(meta.OptionGroups[0].Options[0].ColorTableRows);

        Assert.False(s.G.SetTableFunc!("Mod A", "Pattern", null, "[]"));
        Assert.False(s.G.SetTableFunc!("Mod A", "Nope", "Dots", "[]"));
        Assert.False(s.G.SetTableFunc!("Mod B", null, null, "[]"));
        Assert.False(s.G.SetTableFunc!("Mod A", "Nope", "Dots", "{not json"));   // malformed JSON: false through the guard
        Assert.Single(s.Log.ReceivedCalls(), c => c.GetMethodInfo().Name == "Error");
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.1.R11")]
    [Trait("Plan", "EDGE-contrib-clean-room-7")]
    public void Before_the_first_discovery_everything_is_empty()   // [AC-contrib-clean-room-1] [EDGE-contrib-clean-room-7]
    {
        var s = new Seam();
        Assert.Empty(s.G.OverlaysFunc!());
        Assert.Empty(s.G.ActiveFunc!());
        Assert.Equal("", s.G.GetTableFunc!("a", null, null));
        Assert.False(s.G.SetTableFunc!("a", null, null, "[]"));
    }

    [Fact]
    [Trait("Plan", Plan1)]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.1.R12")]
    public void Duplicate_group_names_propagate_out_of_GetOverlays()   // [AC-contrib-clean-room-1]
    {
        var s = new Seam();
        var meta = Grouped();
        meta.OptionGroups!.Add(new OverlayOptionGroup { PenumbraGroupName = "Pattern" });
        s.Entries = [Entry("a", true, meta)];
        Assert.ThrowsAny<Exception>(() => s.G.OverlaysFunc!());
    }
}
