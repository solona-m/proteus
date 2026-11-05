using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// #62: the built-in toe cap's 2% coverage gate measured the whole sheet (its mask is flat), so feet-only art stayed
/// uncapped and a bikini got a cap. A uniform map is now gated over the toes — the body's toe-weighted skin in UV.
/// </summary>
public class ToeCapToeGateTests
{
    private const int Size = 512;

    /// <summary>A body: a strip of toe-weighted triangles in UV box A (u,v 0.1–0.2) and plain ones in box B (0.6–0.9).</summary>
    private static byte[] Body()
    {
        var p = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>();
        var w = new List<(string, float)[]?>(); var toe = new List<int>(); var rest = new List<int>();
        void Quad(float u0, float u1, float v0, float v1, string bone, List<int> into)
        {
            int b = p.Count;
            foreach (var (u, v) in new[] { (u0, v0), (u1, v0), (u0, v1), (u1, v1) })
            {
                p.Add(new Vector3(u, 0, v)); n.Add(Vector3.UnitY); uv.Add(new Vector2(u, v)); w.Add([(bone, 1f)]);
            }
            into.AddRange([b, b + 1, b + 2, b + 1, b + 3, b + 2]);
        }
        Quad(0.1f, 0.2f, 0.1f, 0.2f, "j_asi_e_l", toe);
        Quad(0.6f, 0.9f, 0.6f, 0.9f, "j_kosi", rest);
        return SyntheticModel.BuildSurface([], new SyntheticModel.SurfaceMesh("/mt_c0201b0001_bibo.mtrl", [.. p], [.. n], [.. uv], [.. w],
                                                                         [(0u, [.. toe, .. rest])]));
    }

    private static byte[] Alpha(float u0, float u1, float v0, float v1)
    {
        var a = new byte[Size * Size];
        for (int y = (int)(v0 * Size); y < (int)(v1 * Size); y++)
            for (int x = (int)(u0 * Size); x < (int)(u1 * Size); x++) a[y * Size + x] = 255;
        return a;
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-4")]
    public void Toe_region_is_the_toe_weighted_skin()
    {
        var region = SecondSkinWriter.ToeRegionMask([Body()], Size);
        Assert.NotNull(region);
        Assert.Equal(255, region![(int)(0.15f * Size) * Size + (int)(0.15f * Size)]);   // box A
        Assert.Equal(0, region[(int)(0.75f * Size) * Size + (int)(0.75f * Size)]);      // box B
        Assert.Null(SecondSkinWriter.ToeRegionMask([Body()], 4));   // too few texels: no region
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-4")]
    public void Uniform_cap_is_gated_over_the_toes()
    {
        var region = SecondSkinWriter.ToeRegionMask([Body()], Size)!;
        var flat = SecondSkinService.NewBuiltInToeCapMask();
        var gate = SecondSkinService.ToeGateRegion(flat, sourceUniform: true, region);
        var feet = Alpha(0.1f, 0.2f, 0.1f, 0.2f);   // about 1% of the sheet, all of the toes
        var bikini = Alpha(0.6f, 0.8f, 0.6f, 0.8f);   // about 4% of the sheet, nowhere near them
        Assert.True(SecondSkinService.ToeShare(flat, feet, Size) < 0.02f);    // the old sheet gate refused the feet
        Assert.True(SecondSkinService.ToeShare(flat, bikini, Size) >= 0.02f); // and took the bikini
        Assert.True(SecondSkinService.ToeShare(gate, feet, Size) >= 0.02f);
        Assert.Equal(0f, SecondSkinService.ToeShare(gate, bikini, Size));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-4")]
    [Trait("Plan", "EDGE-issue-sweep-b-10")]
    [Trait("Plan", "EDGE-issue-sweep-b-11")]
    public void Gate_region_rules()
    {
        var region = new byte[Size * Size];
        var authored = new byte[Size * Size];
        authored[0] = 200;
        Assert.Same(authored, SecondSkinService.ToeGateRegion(authored, sourceUniform: false, region));   // authored: itself
        var flat = SecondSkinService.NewBuiltInToeCapMask();
        Assert.Same(flat, SecondSkinService.ToeGateRegion(flat, sourceUniform: true, null));   // no region: the sheet, as before
        Assert.True(SecondSkinService.IsUniformCap(flat));
        flat[1234] = 10;
        Assert.False(SecondSkinService.IsUniformCap(flat));
        Assert.False(SecondSkinService.IsUniformCap(new byte[16]));   // all black is no cap
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-4")]
    [Trait("Plan", "EDGE-issue-sweep-b-12")]
    public void Cross_uv_remapped_flat_map_counts_as_uniform()
    {
        var source = SecondSkinService.NewBuiltInToeCapMask();
        var remapped = new byte[Size * Size];   // 187 inside the islands, 0 outside, as a cross-UV remap leaves it
        for (int i = 0; i < remapped.Length; i += 3) remapped[i] = SecondSkinService.BuiltInToeCapValue;
        Assert.True(SecondSkinService.IsUniformCap(source));
        Assert.False(SecondSkinService.IsUniformCap(remapped));
        var region = SecondSkinWriter.ToeRegionMask([Body()], Size)!;
        Assert.Same(region, SecondSkinService.ToeGateRegion(remapped, sourceUniform: true, region));
        var svc = Source("Services/SecondSkinService.cs");
        Assert.Contains("sourceUniform = IsUniformCap(RedOf(source));", svc);   // judged on the source before the remap
        Assert.Contains("sharedToeCapUniform = tMap.BuiltIn || ownUniform;", Source("Services/SecondSkinService.ShellSetBuild.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-5")]
    [Trait("Plan", "EDGE-issue-sweep-b-13")]
    public void Toe_gate_outcome_logs_once_per_change()
    {
        var byLayer = new Dictionary<string, string>(StringComparer.Ordinal);
        var gate = new object();
        Assert.True(SecondSkinService.OutcomeChanged(byLayer, gate, "Feet|bibo|built-in", "uncapped:1%"));
        Assert.False(SecondSkinService.OutcomeChanged(byLayer, gate, "Feet|bibo|built-in", "uncapped:1%"));
        Assert.True(SecondSkinService.OutcomeChanged(byLayer, gate, "Feet|bibo|built-in", "placed:40%"));
        Assert.True(SecondSkinService.ShouldLog("placed:40%"));
        Assert.True(SecondSkinService.ShouldLog("uncapped:1%"));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-5")]
    [Trait("Plan", "EDGE-issue-sweep-b-14")]
    public void ToeCapFor_wiring()
    {
        var svc = Source("Services/SecondSkinService.cs");
        int at = svc.IndexOf("private byte[]? ToeCapFor(", StringComparison.Ordinal);
        var body = svc[at..svc.IndexOf("/// A REINFORCED TOE", at, StringComparison.Ordinal)];
        Assert.Contains("if ((d.ToeCapStrength ?? 1f) <= 0f) return null;", body);
        Assert.Contains("if (mask == null || alpha == null) return null;", body);
        Assert.Contains("ToeGateRegion(mask, uniform, toeRegion)", body);
        Assert.Contains("ToeShare(region, alpha, texSize)", body);
        Assert.Contains("of the toes \"", body);
        Assert.Contains("OutcomeChanged(toeGateByLayer, capLogGate, key,", body);
        Assert.Contains("build.sharedToeCapUniform, build.ToeRegionFor(dstType)", Source("Services/SecondSkinService.ShellTextureBake.cs"));
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));
}
