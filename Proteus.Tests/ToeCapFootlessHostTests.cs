using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A host with no bare foot in a look that wants a toe cap: a top or chest shell, or legs under boots. With the built-in
/// cap any body shell can be asked for one, so the writer must drop the request on such a host rather than fall back to
/// generating a cap from a whole-sheet mask (which throws, and loses the shell).
/// </summary>
public sealed class ToeCapFootlessHostTests
{
    private const string Skin = "/mt_c0201b0001_a.mtrl";

    /// <summary>
    /// A skinned patch of body skin: a quad on each side of X (the cap binding resolves each foot on its own side, so an
    /// empty side would miss for the wrong reason), every UV pinned to a small corner of the atlas well away from the
    /// foot island (so a miss comes from the bones and the UVs, not from a fixture that happens to land on the foot).
    /// </summary>
    private static byte[] Patch(string boneLeft, string boneRight)
    {
        var pos = new List<Vector3>();
        var uv = new List<Vector2>();
        var weights = new List<(string, float)[]?>();
        var tris = new List<int>();
        foreach (var (side, bone) in new[] { (1f, boneLeft), (-1f, boneRight) })
        {
            int b = pos.Count;
            float x0 = side * 0.05f, x1 = side * 0.15f;
            pos.AddRange([new(x0, 1.0f, 0.05f), new(x1, 1.0f, 0.05f), new(x1, 1.2f, 0.05f), new(x0, 1.2f, 0.05f)]);
            float u0 = side > 0 ? 0.05f : 0.10f;
            uv.AddRange([new(u0, 0.05f), new(u0 + 0.05f, 0.05f), new(u0 + 0.05f, 0.15f), new(u0, 0.15f)]);
            for (int i = 0; i < 4; i++) weights.Add([(bone, 1f)]);
            tris.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        var normals = Enumerable.Repeat(new Vector3(0, 0, 1), pos.Count).ToArray();
        return SyntheticModel.BuildSurface([],
            new SyntheticModel.SurfaceMesh(Skin, [.. pos], normals, [.. uv], [.. weights], [(0u, [.. tris])]));
    }

    private static List<SecondSkinWriter.AuthoredCapSet> Caps()
    {
        var caps = TestMeshes.CapSets();
        Assert.NotEmpty(caps);   // otherwise the build skips the authored path and this tests nothing
        return caps;
    }

    private static SecondSkinLayer Capped() => new()
    {
        MaterialName = Skin,
        ToeCap = SecondSkinService.NewBuiltInToeCapMask(),
        ToeCapWidth = 512,
        ToeCapHeight = 512,
        ToeCapStrength = 1f,
    };

    // Fresh source bytes for every build: the parse cache keys on array identity, and a build writes into its parse.
    private static (SecondSkinWriter.Stats Stats, List<string> Diag) BuildPlain(string l, string r)
    {
        var diag = new List<string>();
        SecondSkinWriter.Build([Patch(l, r)], [new SecondSkinLayer { MaterialName = Skin }], null, false,
            out var stats, diag: diag.Add, authoredCaps: Caps());
        return (stats, diag);
    }

    private static (SecondSkinWriter.Stats Stats, List<string> Diag) BuildCapped(string l, string r)
    {
        var diag = new List<string>();
        SecondSkinWriter.Build([Patch(l, r)], [Capped()], null, false, out var stats, diag: diag.Add,
            authoredCaps: Caps());
        return (stats, diag);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-42")]
    public void Footless_source_builds_like_no_cap()
    {
        var plain = BuildPlain("j_sebo_a", "j_sebo_a");
        var capped = BuildCapped("j_sebo_a", "j_sebo_a");

        Assert.Null(capped.Stats.CapDeclined);
        Assert.Null(capped.Stats.CapUsed);
        Assert.Contains(capped.Diag, d => d.Contains("none of the bound bones"));
        Assert.Contains(capped.Diag, d => d.Contains("no foot on this host"));
        Assert.Equal(plain.Stats.TrianglesOut, capped.Stats.TrianglesOut);
        Assert.Equal(plain.Stats.VerticesOut, capped.Stats.VerticesOut);
        Assert.Equal(plain.Stats.Bones, capped.Stats.Bones);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-42")]
    [Trait("Plan", "EDGE-toecap-geometry-49")]
    public void Legs_without_toes_take_the_decline_path()
    {
        var plain = BuildPlain("j_asi_c_l", "j_asi_c_r");
        var capped = BuildCapped("j_asi_c_l", "j_asi_c_r");

        // The calf bones are bound by a cap, so a probe reaches the skin: this is a decline, not a footless host.
        Assert.DoesNotContain(capped.Diag, d => d.Contains("none of the bound bones"));
        Assert.Equal(SecondSkinWriter.NoBareFootReason, capped.Stats.CapDeclined);
        Assert.Null(capped.Stats.CapUsed);
        Assert.Equal(plain.Stats.TrianglesOut, capped.Stats.TrianglesOut);
    }

    [Theory]
    [Trait("Plan", "EDGE-toecap-geometry-49")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsNoBareFoot_classifies(bool toeBones, bool want)
        => Assert.Equal(want, SecondSkinWriter.IsNoBareFoot(toeBones));

    [Theory]
    [Trait("Plan", "EDGE-toecap-geometry-49")]
    [InlineData("j_asi_e_l", true)]
    [InlineData("j_asi_e_r", true)]
    [InlineData("iv_asi_oya_a_l", true)]
    [InlineData("j_asi_d_l", false)]
    [InlineData("j_asi_c_r", false)]
    [InlineData("j_sebo_a", false)]
    public void IsToeBone_matches_toes_only(string bone, bool want)
        => Assert.Equal(want, SecondSkinWriter.IsToeBone(bone));

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-48")]
    public void A_foot_no_cap_fits_is_a_decline_not_footless()
    {
        // No synthetic body makes every binding probe empty while carrying toe weights, so the branch is pinned by source:
        // with toe weights and no probe, the host is declined (said once at Information), never silently dropped.
        var src = Source(System.IO.Path.Combine("Services", "SecondSkinWriter.ShellBuild.cs"));
        int footless = src.IndexOf("else if (!HasToeWeights(fitSurface ??= BindSurface(sourceModels)))", System.StringComparison.Ordinal);
        int found = src.IndexOf("a foot was found but no toe cap could be fitted to it", System.StringComparison.Ordinal);
        Assert.True(footless > 0 && found > footless, "the toe-weighted branch follows the footless one");
        Assert.Contains("capDeclined = \"a foot was found", src);
    }

    internal static string Source(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => System.IO.File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", "Proteus", file));
}
