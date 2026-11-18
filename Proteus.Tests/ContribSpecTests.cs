using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using NSubstitute;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The contributors' small features, tested from <c>docs/contrib-clean-room-spec.md</c> §4–§9 only. Behaviour the old
/// code can be reached by without a new seam is gated on the old code; the refresh parse goes through the seam B adds
/// (spec §9.4: the chat handler is private) and carries CleanRoom=NewSeam.
/// </summary>
public class ContribSpecTests
{
    private const string Plan2 = "AC-contrib-clean-room-2";

    // ── A Dalamud plugin interface whose call gates either throw (the plugin is not loaded) or answer InvalidKey ──

    public class GateProxy : DispatchProxy
    {
        public bool Throw;
        public int Invokes;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            string name = targetMethod!.Name;
            if (name is "Subscribe" or "Unsubscribe") return null;
            if (name.StartsWith("Invoke", StringComparison.Ordinal))
            {
                Invokes++;
                if (Throw) throw new InvalidOperationException("not loaded");
            }
            var ret = targetMethod.ReturnType;
            if (ret == typeof(GlamourerApiEc)) return GlamourerApiEc.InvalidKey;
            if (ret == typeof(int)) return (int)GlamourerApiEc.InvalidKey;
            return ret == typeof(void) || !ret.IsValueType ? null : Activator.CreateInstance(ret);
        }
    }

    public class PluginInterfaceProxy : DispatchProxy
    {
        public bool Throw;
        public readonly List<GateProxy> Gates = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var ret = targetMethod!.ReturnType;
            if (targetMethod.Name == "GetIpcSubscriber")
            {
                var gate = (GateProxy)DispatchProxy.Create(ret, typeof(GateProxy));
                gate.Throw = Throw;
                Gates.Add(gate);
                return gate;
            }
            return ret == typeof(void) || !ret.IsValueType ? null : Activator.CreateInstance(ret);
        }

        public static (IDalamudPluginInterface Pi, PluginInterfaceProxy Proxy) Create(bool throwing)
        {
            var pi = DispatchProxy.Create<IDalamudPluginInterface, PluginInterfaceProxy>();
            var proxy = (PluginInterfaceProxy)(object)pi;
            proxy.Throw = throwing;
            return (pi, proxy);
        }
    }

    // ── §4: the refresh words (through the seam B adds) ───────────────────────
    //
    // Seam contract: internal static (bool Full, bool Silent) Plugin.ParseRefreshArguments(string args), where args is
    // the chat command's whole argument string after the handler's trim (it starts with the word refresh).

    [Theory]
    [Trait("Plan", Plan2)]
    [Trait("Plan", "EDGE-contrib-clean-room-9")]
    [Trait("CleanRoom", "NewSeam")]
    [Trait("Seam", "§9.4.Refresh")]
    [InlineData("refresh", false, false)]
    [InlineData("refresh silent", false, true)]
    [InlineData("refresh full", true, false)]
    [InlineData("refresh full silent", true, true)]
    [InlineData("refresh silent full", true, true)]
    [InlineData("REFRESH Silent", false, true)]
    [InlineData("refresh -silent", false, true)]
    [InlineData("refresh -full", true, false)]
    [InlineData("refresh all", true, false)]
    [InlineData("refresh  silent", false, true)]          // consecutive spaces: empty tokens are ignored
    [InlineData("refresh bogus silent bogus", false, true)]
    [InlineData("refresh silent silent", false, true)]
    [InlineData("refresh --silent", false, false)]        // only one leading hyphen counts
    [InlineData("refresh full\tsilent", false, false)]    // tabs are not separators
    [InlineData("refreshsilent", false, true)]            // the prefix quirk
    [InlineData("refreshfull", true, false)]
    public void Refresh_arguments_follow_the_spec(string args, bool full, bool silent)   // [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-9]
    {
        var parse = typeof(Plugin).GetMethod("ParseRefreshArguments", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(parse);
        var result = ((bool Full, bool Silent))parse!.Invoke(null, [args])!;
        Assert.Equal((full, silent), result);
    }

    // ── §7: opening a mod in Penumbra ─────────────────────────────────────────

    [Fact]
    [Trait("Plan", Plan2)]
    [Trait("Plan", "EDGE-contrib-clean-room-12")]
    public void Opening_a_mod_in_Penumbra_follows_the_spec()   // [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-12]
    {
        var (pi, proxy) = PluginInterfaceProxy.Create(throwing: true);
        var bridge = new PenumbraBridge(pi, Substitute.For<IPluginLog>());
        Assert.False(bridge.IsAvailable);
        int before = proxy.Gates.Sum(g => g.Invokes);

        var ex = Record.Exception(() => bridge.OpenToMod("Some Mod"));

        Assert.Null(ex);
        Assert.Equal(before, proxy.Gates.Sum(g => g.Invokes));   // Penumbra absent: nothing is invoked
    }

    // ── §5: multi-select and single-select groups with nothing selected ───────

    [Fact]
    [Trait("Plan", Plan2)]
    [Trait("Plan", "EDGE-contrib-clean-room-10")]
    public void Multi_select_group_defaults_follow_the_spec()   // [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-10]
    {
        // Penumbra's settings cannot be read, so every group contributes nothing: no fallback to the first option.
        var (pi, _) = PluginInterfaceProxy.Create(throwing: true);
        var discovery = new SidecarDiscoveryService(new PenumbraBridge(pi, Substitute.For<IPluginLog>()), Substitute.For<IPluginLog>());
        var meta = new ProteusMetadata
        {
            OptionGroups =
            [
                new OverlayOptionGroup { PenumbraGroupName = "Multi", Options = [new OverlayOption { Name = "First", Overlays = [new OverlayDescriptor()] }] },
                new OverlayOptionGroup { PenumbraGroupName = "Single", Options = [new OverlayOption { Name = "Only", Overlays = [new OverlayDescriptor()] }] },
            ],
        };
        var entry = new OverlayEntry("mod", "Mod", 1, true, meta, "sidecar");

        Assert.Empty(discovery.ResolveActiveOverlays(entry));
    }

    [Fact]
    [Trait("Plan", Plan2)]
    public void Resolved_overlay_record_keeps_its_positional_shape()   // [AC-contrib-clean-room-2]
    {
        // §5/§9.2: descriptor, colour rows, group, option, then the group order defaulting to the largest int.
        var d = new OverlayDescriptor();
        var rows = new List<ColorTableRowPreset>();
        var r = new ResolvedOverlay(d, rows, "Group", "Option");
        Assert.Equal((d, rows, "Group", "Option", int.MaxValue), (r.Descriptor, r.ColorTableRows, r.OptionGroup, r.Option, r.GroupOrder));
    }

    // ── §6.4: the body-shape signature (private; reached by reflection, spec §9.4) ──

    public static IEnumerable<object?[]> Signatures() =>
    [
        [null, ""],
        [new Dictionary<string, HashSet<string>>(), ""],
        [new Dictionary<string, HashSet<string>> { ["z"] = ["c"], ["a"] = ["b", "a"] }, "a,b|c"],
        [new Dictionary<string, HashSet<string>> { ["k1"] = ["x"], ["k2"] = ["x"] }, "x|x"],
        [new Dictionary<string, HashSet<string>> { ["k"] = ["B", "a"] }, "B,a"],   // ordinal: upper case first
    ];

    [Theory]
    [Trait("Plan", Plan2)]
    [Trait("Plan", "EDGE-contrib-clean-room-18")]
    [MemberData(nameof(Signatures))]
    public void Body_shape_signature_follows_the_spec(Dictionary<string, HashSet<string>>? shapes, string expected)   // [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-18]
    {
        var m = typeof(CompositorService).GetMethod("BodyShapeSignature", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(m);
        Assert.Equal(expected, (string)m!.Invoke(null, [shapes])!);
    }

    // ── §8.2: lock conflicts logged ───────────────────────────────────────────

    private static int Warnings(IPluginLog log) => log.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Warning");

    private static string WarningText(IPluginLog log) => string.Join(" | ", log.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == "Warning")
        .SelectMany(c => c.GetArguments())
        .SelectMany(a => a is object?[] arr ? arr : [a])
        .Select(a => a?.ToString() ?? ""));

    private static (GlamourerBridge Bridge, IPluginLog Log) LockedGlamourer()
    {
        var (pi, _) = PluginInterfaceProxy.Create(throwing: false);   // every call answers InvalidKey
        var objects = Substitute.For<IObjectTable>();                  // LocalPlayer: a non-null substitute
        var log = Substitute.For<IPluginLog>();
        var bridge = new GlamourerBridge(pi, objects, log);
        log.ClearReceivedCalls();
        return (bridge, log);
    }

    [Fact]
    [Trait("Plan", Plan2)]
    [Trait("Plan", "EDGE-contrib-clean-room-11")]
    public void Glamourer_lock_conflict_is_logged_once_per_refused_call()   // [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-11]
    {
        var (bridge, log) = LockedGlamourer();
        Assert.True(bridge.IsAvailable);

        Assert.False(bridge.ReapplyPlayerState());
        Assert.Equal(1, Warnings(log));
        Assert.False(bridge.ReapplyPlayerState());
        Assert.Equal(2, Warnings(log));   // no de-duplication
    }

    [Fact]
    [Trait("Plan", Plan2)]
    [Trait("Plan", "EDGE-contrib-clean-room-11")]
    public void Glamourer_lock_warnings_name_the_item_and_slot()   // [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-11]
    {
        var (bridge, log) = LockedGlamourer();

        Assert.False(bridge.SetGlasses(4242UL));
        Assert.Equal(1, Warnings(log));
        Assert.Contains("4242", WarningText(log));

        log.ClearReceivedCalls();
        Assert.False(bridge.SetAccessory(31337UL, "nek"));   // §9.3: the game slot code; the warning names Glamourer's slot
        Assert.Equal(1, Warnings(log));
        Assert.Contains("31337", WarningText(log));
        Assert.Contains("Neck", WarningText(log));
    }
}
