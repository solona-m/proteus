using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A woman's overlay onto a man through <see cref="MensOverlayRemap"/>, and the composite's wiring of it: one decision
/// (<c>SourceHere</c>) whose result every remap site passes along and caches by.
/// </summary>
public class MensOverlayRemapTests
{
    private sealed class FakeHost(UVRemapService uv, byte[] image) : IMensRemapHost
    {
        public readonly List<string> Keys = [];
        public readonly Dictionary<string, byte[]?> Cache = [];
        public UVRemapService UvRemap => uv;
        public byte[]? Load(string path, int w, int h, ResampleFilter filter) => image;
        public byte[]? GetOrDerive(string path, string key, int w, int h, Func<byte[]?> make)
        {
            string k = $"{path}|{key}|{w}x{h}";
            Keys.Add(k);
            if (Cache.TryGetValue(k, out var hit)) return hit;
            var made = make();
            if (made != null) Cache[k] = made;
            return made;
        }
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Bibo_to_tbse_uses_bibo_to_tbse_through_GetOrDerive() // [AC-tbse-uvmap-3]
    {
        // No maps in the folder: the conversion is attempted through the host's cache and fails cleanly, uncached.
        var dir = Path.Combine(Path.GetTempPath(), "proteus-mensremap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "uvmaps"));
        try
        {
            var host = new FakeHost(new UVRemapService(Substitute.For<IPluginLog>(), dir), new byte[4096 * 4096 * 4]);
            var route = new MensRoute("bibo", UVRemapService.TbseSpace);
            Assert.Null(MensOverlayRemap.Convert(host, 2048, 2048, route, "a.png", ResampleFilter.Auto));
            Assert.Single(host.Keys);
            Assert.Contains("m2:tbse<bibo|a", host.Keys[0]);
            Assert.Empty(host.Cache);
            Assert.Null(MensOverlayRemap.Convert(host, 2048, 2048, route, null, ResampleFilter.Auto));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-5")]
    public void Null_route_leaves_RemapIfNeededCore_on_todays_path() // [AC-tbse-uvmap-5]
    {
        // Only a men's key takes the men's branch; every type a man's own art (or anyone's) has today parses to none.
        foreach (var t in new[] { null, "bibo", "gen3", "gen2", "tbse", "male vanilla", "face", "facelr" })
            Assert.Null(UVRemapService.ParseMensKey(t));
        var key = UVRemapService.MensKey(new MensRoute("gen3", "tbse"), "gen3");
        Assert.Equal((new MensRoute("gen3", "tbse"), "gen3"), UVRemapService.ParseMensKey(key));
        Assert.Equal((new MensRoute("bibo", "male vanilla"), (string?)null),
                     UVRemapService.ParseMensKey(UVRemapService.MensKey(new MensRoute("bibo", "male vanilla"), null)));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-14")]
    public void Derive_keys_distinct_per_route_filter_and_size() // [EDGE-tbse-uvmap-14]
    {
        var keys = new[]
        {
            MensOverlayRemap.DeriveKey(new MensRoute("bibo", "tbse"), ResampleFilter.Auto),
            MensOverlayRemap.DeriveKey(new MensRoute("bibo", "tbse"), ResampleFilter.Nearest),
            MensOverlayRemap.DeriveKey(new MensRoute("gen3", "tbse"), ResampleFilter.Auto),
            MensOverlayRemap.DeriveKey(new MensRoute("bibo", "male vanilla"), ResampleFilter.Auto),
        };
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.All(keys, k => Assert.DoesNotContain("gen2<", k));
    }

    private static string Source(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Proteus", "Services", file);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException(file);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Mask_claim_and_silhouette_sites_take_the_route() // [AC-tbse-uvmap-3]
    {
        var mc = Source("CompositorService.MaterialComposite.cs");
        var ob = Source("CompositorService.OverlayBlendPass.cs");
        // The three places a source type is decided all end in SourceHere; the masks, AO and index merges read the
        // decided type (lastSrcBodyTypeByMod, SrcTypeOf), never a fresh inference.
        Assert.Contains("srcBodyType = material.SourceHere(desc, srcBodyType);", ob);
        Assert.Contains("gSrc = SourceHere(gd, gSrc);", mc);
        Assert.Equal(3, Regex.Matches(mc, @"return SourceHere\(d, ").Count);
        Assert.Contains("material.lastSrcBodyTypeByMod[entry.ModDirectory] = srcBodyType;", ob);
        Assert.True(ob.IndexOf("material.SourceHere(desc, srcBodyType)", StringComparison.Ordinal)
                    < ob.IndexOf("material.lastSrcBodyTypeByMod[entry.ModDirectory] = srcBodyType;", StringComparison.Ordinal));
        // The men's branch is RemapIfNeededCore's first statement; the women's body below it is reached with the legacy type.
        int core = mc.IndexOf("private byte[]? RemapIfNeededCore(", StringComparison.Ordinal);
        int guard = mc.IndexOf("UVRemapService.ParseMensKey(srcType)", core, StringComparison.Ordinal);
        int legacy = mc.IndexOf("if (png == null || srcType == null) return png;", core, StringComparison.Ordinal);
        Assert.True(core < guard && guard < legacy);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Caches_key_on_the_route() // [AC-tbse-uvmap-3]
    {
        // The combined-mask cache and the shell's remap cache key on the source type string, which carries the route; the
        // shell geometry key names the men's space when a man's vanilla parts were un-mirrored by the men's maps.
        var mc = Source("CompositorService.MaterialComposite.cs");
        var ss = Source("SecondSkinService.cs");
        var sb = Source("SecondSkinService.ShellSetBuild.cs");
        Assert.Contains("var bodyKey = $\"{srcBodyType ?? \"\"}→{dstBodyType ?? dstFaceSpace ?? \"\"}\";", mc);
        Assert.Contains("var key = (path, srcType, dstType, w, h, filter);", ss);
        Assert.Contains("menUnmirror && IsGen2(b.Uv) ? UVRemapService.MaleVanillaSpace : b.Uv", sb);
        Assert.NotEqual(UVRemapService.MensKey(new MensRoute("bibo", "tbse"), "bibo"), "bibo");
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-12")]
    public void Seam_drop_skipped_for_male_vanilla() // [EDGE-tbse-uvmap-12]
    {
        var ob = Source("CompositorService.OverlayBlendPass.cs");
        Assert.Contains("men.Route.Dst == UVRemapService.TbseSpace ? (\"bibo\", UVRemapService.TbseSpace) : (null, null)", ob);
        Assert.Contains("ComputeSeamDropMask(decision, covW, covH, seamSrc, seamDst)", ob);
    }
}
