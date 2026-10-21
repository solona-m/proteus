using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Timing tests, run alone: xunit runs a collection that disables parallelisation after every parallel one has finished,
/// so the rest of the suite is not competing for the CPU while they measure. They report, they don't assert: the same
/// route measured 2.7–3.4 s depending on what ran before it in the process, so a fixed budget only flakes. The in-game
/// refresh timeline is the real check.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing, alone";
}

/// <summary>What carrying a woman's overlay onto a man costs (see WomensArtOnMen): the route, timed against its budget.</summary>
[Collection(TimingCollection.Name)]
public class MensRouteTimingTests(ITestOutputHelper output)
{
    [LocalDataFact(LocalData.TbseUvMaps)]
    public void Stockings_route_cost_is_reported()
    {
        // Five 4096 sheets (Cotton diffuse/normal/index and two masks) carried bibo→TBSE. Decoding is the same for a woman,
        // so only the route is timed: cold includes reading the map, warm is the decode cache's hit.
        var uv = new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));
        var rnd = new Random(7);
        var sheets = Enumerable.Range(0, 5).Select(_ => { var b = new byte[4096 * 4096 * 4]; rnd.NextBytes(b); return b; }).ToList();

        var sw = Stopwatch.StartNew();
        var results = sheets.Select(s => uv.ConvertSheet(s, 4096, 4096, "bibo", "tbse")).ToList();
        double cold = sw.Elapsed.TotalMilliseconds;
        Assert.All(results, r => Assert.NotNull(r));

        sw.Restart();
        _ = sheets.Select(s => uv.ConvertSheet(s, 4096, 4096, "bibo", "tbse")).ToList();
        double mapsLoaded = sw.Elapsed.TotalMilliseconds;

        // Warm: the same five through MensOverlayRemap.Convert, a second time, where the composite's decode cache answers.
        var host = new CachingHost(uv, sheets);
        var route = new MensRoute("bibo", "tbse");
        for (int i = 0; i < sheets.Count; i++) Assert.NotNull(MensOverlayRemap.Convert(host, 4096, 4096, route, $"s{i}", ResampleFilter.Auto, out _));
        sw.Restart();
        for (int i = 0; i < sheets.Count; i++) Assert.NotNull(MensOverlayRemap.Convert(host, 4096, 4096, route, $"s{i}", ResampleFilter.Auto, out _));
        double warm = sw.Elapsed.TotalMilliseconds;

        output.WriteLine($"cold {cold:F0} ms (incl. map read), maps loaded {mapsLoaded:F0} ms, warm (cache hit) {warm:F1} ms");
    }

    private sealed class CachingHost(UVRemapService uv, List<byte[]> sheets) : IMensRemapHost
    {
        private readonly Dictionary<string, byte[]?> cache = [];
        public UVRemapService UvRemap => uv;
        public byte[]? Load(string path, int w, int h, ResampleFilter filter) => sheets[int.Parse(path[1..])];
        public byte[]? GetOrDerive(string path, string key, int w, int h, Func<byte[]?> make)
        {
            string k = $"{path}|{key}|{w}x{h}";
            if (!cache.TryGetValue(k, out var v)) cache[k] = v = make();
            return v;
        }
    }
}
