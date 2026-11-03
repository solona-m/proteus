using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Where a mod's toe cap comes from (its own Toe Cap option's map, or the built-in cap its "Cap the toes" setting asks
/// for), how one composite resolves it once and shares it, and how the shell build picks and logs the shared cap.
/// </summary>
public sealed class ToeCapSourceTests : IDisposable
{
    private readonly string sidecar = Path.Combine(Path.GetTempPath(), "proteus-capsrc-" + Guid.NewGuid().ToString("N"));

    public ToeCapSourceTests() => Directory.CreateDirectory(Path.Combine(sidecar, "Masks"));

    public void Dispose()
    {
        try { Directory.Delete(sidecar, recursive: true); } catch { /* temp */ }
    }

    private void WriteMap(string option) => File.WriteAllBytes(Path.Combine(sidecar, "Masks", option + ".png"), [0]);

    // ── which map ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-9")]
    public void ChooseToeCap_falls_back_to_the_built_in_cap()
    {
        var (map, missing) = SidecarDiscoveryService.ChooseToeCap(["Seam"], sidecar, capToes: true);
        Assert.Same(ToeCapMap.Default, map);
        Assert.True(map!.BuiltIn);
        Assert.Null(map.OwnPath);
        Assert.Null(missing);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-25")]
    public void ChooseToeCap_option_map_wins_over_flag()
    {
        WriteMap("Toe Cap");
        var (map, _) = SidecarDiscoveryService.ChooseToeCap(["Toe Cap"], sidecar, capToes: true);
        Assert.False(map!.BuiltIn);
        Assert.EndsWith(Path.Combine("Masks", "Toe Cap.png"), map.OwnPath);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-2")]
    public void ChooseToeCap_missing_option_map_falls_to_flag()
    {
        var (withFlag, missing) = SidecarDiscoveryService.ChooseToeCap(["Toe Cap"], sidecar, capToes: true);
        Assert.Same(ToeCapMap.Default, withFlag);
        Assert.Equal("Toe Cap", missing);

        var (without, missing2) = SidecarDiscoveryService.ChooseToeCap(["Toe Cap"], sidecar, capToes: false);
        Assert.Null(without);
        Assert.Equal("Toe Cap", missing2);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-26")]
    public void ChooseToeCap_nothing_without_option_or_flag()
    {
        Assert.Equal((null, null), SidecarDiscoveryService.ChooseToeCap(["Seam"], sidecar, capToes: false));
        Assert.Equal((null, null), SidecarDiscoveryService.ChooseToeCap(null, sidecar, capToes: false));
    }

    // ── the setting on disk ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-4")]
    [Trait("Plan", "EDGE-toecap-geometry-33")]
    public void CapToes_json_round_trip()
    {
        var on = JsonSerializer.Serialize(new ProteusMetadata { CapToes = true }, ProteusJson.MetadataWrite);
        Assert.Contains("\"CapToes\": true", on);
        var off = JsonSerializer.Serialize(new ProteusMetadata { CapToes = null }, ProteusJson.MetadataWrite);
        Assert.DoesNotContain("CapToes", off);
        Assert.Null(JsonSerializer.Deserialize<ProteusMetadata>("{}", ProteusJson.MetadataRead)!.CapToes);
        Assert.True(JsonSerializer.Deserialize<ProteusMetadata>(on, ProteusJson.MetadataRead)!.CapToes);
    }

    // ── one answer per composite ────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-1")]
    [Trait("Plan", "EDGE-toecap-geometry-40")]
    public void Snapshot_is_built_once_with_wanted_from_option_and_maps()
    {
        var own = new ToeCapMap("x/Masks/Toe Cap.png");
        var s = ToeCapSnapshot.From([("Stockings", own), ("Socks", ToeCapMap.Default), ("Shirt", null)]);
        Assert.True(s.Wanted.SetEquals(["stockings", "SOCKS"]));
        Assert.True(s.FromOption.SetEquals(["Stockings"]));
        Assert.Same(own, s.Maps["STOCKINGS"]);
        Assert.False(s.Maps.ContainsKey("Shirt"));
        Assert.IsAssignableFrom<IReadOnlySet<string>>(s.Wanted);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-30")]
    public void Snapshot_from_option_excludes_built_in()
    {
        var s = ToeCapSnapshot.From([("Socks", ToeCapMap.Default)]);
        Assert.Contains("Socks", s.Wanted);
        Assert.DoesNotContain("Socks", s.FromOption);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-46")]
    public void Snapshot_empty_is_empty_and_published_with_no_mods()
    {
        Assert.Empty(ToeCapSnapshot.Empty.Wanted);
        Assert.Empty(ToeCapSnapshot.Empty.FromOption);
        Assert.Empty(ToeCapSnapshot.Empty.Maps);

        var run = ToeCapFootlessHostTests.Source(Path.Combine("Services", "CompositorService.CompositeRun.cs"));
        int branch = run.IndexOf("if (entries.Count == 0)", StringComparison.Ordinal);
        Assert.True(branch > 0);
        Assert.Contains("compositor.ClearToeCapSnapshot();", run.Substring(branch, 400));
        var comp = ToeCapFootlessHostTests.Source(Path.Combine("Services", "CompositorService.Compositing.cs"));
        Assert.Contains("ClearToeCapSnapshot() => _toeCapSnapshot = ToeCapSnapshot.Empty", comp);
    }

    // ── the shared cap in the shell build ───────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-9")]
    [Trait("Plan", "EDGE-toecap-geometry-29")]
    [Trait("Plan", "EDGE-toecap-geometry-41")]
    public void SharedCapMask_built_in_is_full_and_never_reads()
    {
        var mask = SecondSkinService.SharedCapMask(ToeCapMap.Default, _ => throw new InvalidOperationException("read"))!;
        Assert.Equal(512 * 512, mask.Length);
        Assert.All(mask, b => Assert.Equal(SecondSkinService.BuiltInToeCapValue, b));
        Assert.True(SecondSkinService.BuiltInToeCapValue >= 128);   // every reader thresholds at 128

        var reads = new List<string>();
        var own = SecondSkinService.SharedCapMask(new ToeCapMap("a/Masks/Toe Cap.png"), p => { reads.Add(p); return [1]; });
        Assert.Equal([1], own);
        Assert.Equal(["a/Masks/Toe Cap.png"], reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-44")]
    public void Built_in_mask_is_fresh_per_call()
    {
        var a = SecondSkinService.NewBuiltInToeCapMask();
        var b = SecondSkinService.NewBuiltInToeCapMask();
        Assert.NotSame(a, b);
        Assert.Equal(a, b);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-28")]
    public void OrderCapCandidates_puts_own_maps_first_stably()
    {
        var own1 = new ToeCapMap("1"); var own2 = new ToeCapMap("2");
        var ordered = SecondSkinService.OrderCapCandidates(
            [("A", ToeCapMap.Default), ("B", own1), ("C", ToeCapMap.Default), ("D", own2)]).Select(c => c.Mod);
        Assert.Equal(["B", "D", "A", "C"], ordered);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-45")]
    public void CapCandidates_follow_priority_order_not_the_dictionary()
    {
        var caps = new Dictionary<string, ToeCapMap>(StringComparer.OrdinalIgnoreCase)
        {
            ["Z"] = ToeCapMap.Default, ["Y"] = new ToeCapMap("y"), ["X"] = ToeCapMap.Default, ["W"] = new ToeCapMap("w"),
        };
        var got = SecondSkinService.CapCandidates(["w", "x", "y", "Shirt", "z", "W"], caps).Select(c => c.Mod);
        Assert.Equal(["w", "y", "x", "z"], got);
        Assert.Empty(SecondSkinService.CapCandidates(["w"], null));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-47")]
    public void No_authored_caps_gives_layers_no_toe_cap()
    {
        int calls = 0;
        byte[]? Cap() { calls++; return [1]; }
        Assert.Null(SecondSkinService.LayerToeCap(isBody: true, capsAvailable: false, Cap));
        Assert.Null(SecondSkinService.LayerToeCap(isBody: false, capsAvailable: true, Cap));
        Assert.Equal(0, calls);
        Assert.Equal([1], SecondSkinService.LayerToeCap(isBody: true, capsAvailable: true, Cap));
        Assert.Equal(1, calls);

        var bake = ToeCapFootlessHostTests.Source(Path.Combine("Services", "SecondSkinService.ShellTextureBake.cs"));
        Assert.Contains("LayerToeCap(layerSurf.Key.IsBody, build.capsAvailable,", bake);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-47")]
    public void Generated_path_still_reachable_for_direct_writer_calls()
    {
        // Why the no-caps guard lives in the service: the goldens build a toe cap with no authored caps on purpose.
        var golden = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ThisFile())!, "GoldenTests.cs"));
        Assert.Contains("toecap-generated", golden);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;

    // ── logging the outcome ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-37")]
    [Trait("Plan", "EDGE-toecap-geometry-43")]
    public void OutcomeChanged_is_per_host_and_on_change()
    {
        var byHost = new Dictionary<string, string>();
        var gate = new object();
        int logged = 0;
        for (int round = 0; round < 3; round++)
        {
            if (SecondSkinService.OutcomeChanged(byHost, gate, "e0000/sho", "used:bibo (98% placed)")) logged++;
            if (SecondSkinService.OutcomeChanged(byHost, gate, "e0000/dwn", "declined:" + SecondSkinWriter.NoBareFootReason)) logged++;
        }
        Assert.Equal(2, logged);
        Assert.True(SecondSkinService.OutcomeChanged(byHost, gate, "e0000/dwn",
            "declined:37% of the toe cap could not be placed on this body"));

        // placed → footless → placed logs the placement again; "none" itself is never logged.
        var seq = new[] { "used:bibo (98% placed)", "none", "used:bibo (98% placed)" }
            .Select(o => SecondSkinService.OutcomeChanged(byHost, gate, "e0000/top", o) && SecondSkinService.ShouldLog(o))
            .ToList();
        Assert.Equal([true, false, true], seq);

        int first = 0;
        Parallel.For(0, 64, _ =>
        {
            if (SecondSkinService.OutcomeChanged(byHost, gate, "new/host", "used:yab (99% placed)"))
                Interlocked.Increment(ref first);
        });
        Assert.Equal(1, first);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-47")]
    public void LogLineChanged_warns_once_per_folder()
    {
        string? last = null;
        Assert.True(SecondSkinService.LogLineChanged(ref last, "C:/p/Meshes"));
        Assert.False(SecondSkinService.LogLineChanged(ref last, "C:/p/Meshes"));
    }

    // ── loading the authored caps ───────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-50")]
    public void LoadAuthoredCaps_reads_the_shipped_caps()
    {
        var caps = SecondSkinService.LoadAuthoredCaps(AppContext.BaseDirectory, Substitute.For<IPluginLog>());
        Assert.NotEmpty(caps);
        Assert.IsType<SecondSkinWriter.AuthoredCapSet[]>(caps);
        Assert.Equal(TestMeshes.CapSets().Select(c => c.Name), caps.Select(c => c.Name));
        Assert.Empty(SecondSkinService.LoadAuthoredCaps(null, Substitute.For<IPluginLog>()));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-50")]
    public void AuthoredCaps_is_published_once()
    {
        var src = ToeCapFootlessHostTests.Source(Path.Combine("Services", "SecondSkinService.cs"));
        Assert.Contains("Lazy<IReadOnlyList<SecondSkinWriter.AuthoredCapSet>> authoredCaps", src);
        Assert.Contains("LazyThreadSafetyMode.ExecutionAndPublication", src);
        Assert.DoesNotContain("authoredCapTried", src);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-50")]
    public void Lazy_load_is_shared_under_contention()
    {
        var lazy = new Lazy<IReadOnlyList<SecondSkinWriter.AuthoredCapSet>>(
            () => SecondSkinService.LoadAuthoredCaps(AppContext.BaseDirectory, Substitute.For<IPluginLog>()),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var seen = new IReadOnlyList<SecondSkinWriter.AuthoredCapSet>[64];
        Parallel.For(0, 64, i => seen[i] = lazy.Value);
        Assert.All(seen, s => Assert.Same(seen[0], s));
        Assert.NotEmpty(seen[0]);
    }
}
