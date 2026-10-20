using System;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which art takes a men's route, and where the route carries it — on small synthetic maps whose answers are known:
/// bibo↔TBSE mirror the sheet left to right, bibo↔gen3 flip it top to bottom.
/// </summary>
public class MensRouteTests : IDisposable
{
    private const int S = 16;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "proteus-mensroute-" + Guid.NewGuid().ToString("N"));
    private readonly IPluginLog log = Substitute.For<IPluginLog>();

    public MensRouteTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "uvmaps"));
        Write("bibo_to_tbse", (x, y) => (S - 1 - x, y));
        Write("tbse_to_bibo", (x, y) => (S - 1 - x, y));
        Write("bibo_to_gen3", (x, y) => (x, S - 1 - y));
        Write("gen3_to_bibo", (x, y) => (x, S - 1 - y));
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>A map whose pixel (x, y) stores the source pixel <paramref name="f"/>(x, y).</summary>
    private void Write(string name, Func<int, int, (int X, int Y)> f)
    {
        var r = new ushort[S * S]; var g = new ushort[S * S]; var ok = new bool[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                var (sx, sy) = f(x, y);
                int i = y * S + x;
                r[i] = UvTransferBake.Quantize((float)sx / (S - 1));
                g[i] = UvTransferBake.Quantize((float)sy / (S - 1));
                ok[i] = true;
            }
        UvTransferMapFile.WriteAtomic(Path.Combine(dir, "uvmaps", name + "_transfer.tif"), UvTransferMapFile.Encode(r, g, ok, S));
    }

    private UVRemapService Service() => new(log, dir);

    /// <summary>A w×h image, black but for one white pixel at (x, y).</summary>
    private static byte[] Dot(int w, int h, int x, int y)
    {
        var img = new byte[w * h * 4];
        int o = (y * w + x) * 4;
        img[o] = img[o + 1] = img[o + 2] = img[o + 3] = 255;
        return img;
    }

    private static (int X, int Y) WhereIs((byte[] Rgba, int W, int H) img)
    {
        int best = -1, at = -1;
        for (int i = 0; i < img.W * img.H; i++)
            if (img.Rgba[i * 4] > best) { best = img.Rgba[i * 4]; at = i; }
        return (at % img.W, at / img.W);
    }

    private static OverlayDescriptor Art(string? declared, params string[] paths)
        => new() { SourceBodyType = declared, MaterialGamePaths = [.. paths] };

    private const string BiboSkin = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
    private const string WomanGen3 = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_b.mtrl";
    private const string ManGen3 = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl";

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Bibo_saved_and_undeclared_womens_art_route_to_tbse() // [AC-tbse-uvmap-3]
    {
        Assert.Equal(new MensRoute("bibo", "tbse"), UVRemapService.MensRouteFor("bibo", "bibo", [ManGen3], "tbse"));
        Assert.Equal(new MensRoute("bibo", "tbse"), UVRemapService.MensRouteFor(null, "bibo", [BiboSkin], "tbse"));
        Assert.Equal(new MensRoute("gen3", "tbse"), UVRemapService.MensRouteFor(null, "gen3", [WomanGen3, ManGen3], "tbse"));
        Assert.Equal(new MensRoute("bibo", "male vanilla"), UVRemapService.MensRouteFor("bibo", "bibo", [], "male vanilla"));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Bibo_onto_tbse_uses_bibo_to_tbse() // [AC-tbse-uvmap-3]
    {
        var got = Service().ConvertSheet(Dot(S, S, 3, 5), S, S, "bibo", "tbse", S);
        Assert.NotNull(got);
        Assert.Equal((S - 1 - 3, 5), WhereIs(got!.Value));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-4")]
    public void Bibo_onto_male_vanilla_is_tbse_right_half() // [AC-tbse-uvmap-4]
    {
        var got = Service().ConvertSheet(Dot(S, S, 3, 5), S, S, "bibo", UVRemapService.MaleVanillaSpace, S);
        Assert.NotNull(got);
        Assert.Equal((S / 2, S), (got!.Value.W, got.Value.H));
        Assert.Equal((S - 1 - 3 - S / 2, 5), WhereIs(got.Value));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-5")]
    public void Own_art_has_no_route() // [AC-tbse-uvmap-5]
    {
        // Saved gen3/gen2 on a man is his (answer 1), as is art naming only men's bodies or declared in a men's layout.
        Assert.Null(UVRemapService.MensRouteFor("gen3", "gen3", [WomanGen3], "tbse"));
        Assert.Null(UVRemapService.MensRouteFor("gen2", "gen2", [WomanGen3], "male vanilla"));
        Assert.Null(UVRemapService.MensRouteFor(null, "gen3", [ManGen3], "tbse"));
        Assert.Null(UVRemapService.MensRouteFor("tbse", "tbse", [], "tbse"));
        Assert.Null(UVRemapService.MensRouteFor(UVRemapService.MaleVanillaSpace, null, [], "tbse"));
        Assert.Null(UVRemapService.MensRouteFor(null, null, [BiboSkin], "tbse"));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-5")]
    public void No_route_used_without_maps_and_request_only_when_route_needs_a_mens_map() // [AC-tbse-uvmap-5]
    {
        var uv = Service();
        int asked = 0;
        uv.MenMapsWanted += () => asked++;
        // His own art on his other sibling: no route, so nothing is ever asked for.
        Assert.Null(UVRemapService.MensRouteFor(null, "gen3", [ManGen3], UVRemapService.MaleVanillaSpace));
        Assert.Equal(0, asked);
        uv.RequestMenMaps();
        uv.RequestMenMaps();
        Assert.Equal(1, asked);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-10")]
    public void Tbse_and_male_vanilla_art_convert_onto_bibo_gen3_and_gen2() // [AC-tbse-uvmap-10]
    {
        var uv = Service();
        Assert.Equal((S - 1 - 3, 5), WhereIs(uv.ConvertSheet(Dot(S, S, 3, 5), S, S, "tbse", "bibo", S)!.Value));
        Assert.Equal((S - 1 - 3, S - 1 - 5), WhereIs(uv.ConvertSheet(Dot(S, S, 3, 5), S, S, "tbse", "gen3", S)!.Value));
        var gen2 = uv.ConvertSheet(Dot(S, S, 3, 5), S, S, "tbse", "gen2", S)!.Value;
        Assert.Equal((S - 1 - 3 - S / 2, 5), WhereIs(gen2));
        // Men's vanilla art spread over the TBSE sheet first: its dot lands on both halves, so on both sides of bibo.
        var fromVanilla = uv.ConvertSheet(Dot(S / 2, S, 1, 5), S / 2, S, UVRemapService.MaleVanillaSpace, "bibo", S)!.Value;
        int lit = Enumerable.Range(0, S * S).Count(i => fromVanilla.Rgba[i * 4] > 128);
        Assert.InRange(lit, 2, 8);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-8")]
    public void Mixed_sex_undeclared_uses_womens_rule_mens_only_is_own() // [EDGE-tbse-uvmap-8]
    {
        Assert.NotNull(UVRemapService.MensRouteFor(null, "gen3", [ManGen3, WomanGen3], "tbse"));
        Assert.Null(UVRemapService.MensRouteFor(null, "gen3", [ManGen3, "chara/human/c0301/obj/body/b0001/material/v0001/mt_c0301b0001_b.mtrl"], "tbse"));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-9")]
    public void Importer_saved_layouts_on_a_man_have_no_route() // [EDGE-tbse-uvmap-9]
    {
        // Luminis' wearer fallback and the emissive importer save the wearer's reading (gen3 on TBSE); Onion saves gen2 for
        // vanilla and gen3 for gen3 layers. Every one lists every race's material.
        string[] allRaces = [ManGen3, WomanGen3, "chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_b.mtrl"];
        Assert.Null(UVRemapService.MensRouteFor("gen3", "gen3", allRaces, "tbse"));
        Assert.Null(UVRemapService.MensRouteFor("gen2", "gen2", allRaces, UVRemapService.MaleVanillaSpace));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-10")]
    public void Own_art_between_b_and_a_has_no_route_and_no_request() // [EDGE-tbse-uvmap-10]
    {
        Assert.Null(UVRemapService.MensRouteFor(null, "gen3", [ManGen3], UVRemapService.MaleVanillaSpace));
        Assert.Null(UVRemapService.MensRouteFor(null, "gen2", ["chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl"], "tbse"));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-11")]
    public void Gen3_to_tbse_chains_via_bibo_without_probing() // [EDGE-tbse-uvmap-11]
    {
        Assert.Equal([("gen3", "bibo"), ("bibo", "tbse")], UVRemapService.Hops("gen3", "tbse")!);
        var got = Service().ConvertSheet(Dot(S, S, 3, 5), S, S, "gen3", "tbse", S);
        Assert.Equal((S - 1 - 3, S - 1 - 5), WhereIs(got!.Value));
        log.DidNotReceiveWithAnyArgs().Warning(default(string)!, default(object[])!);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-11")]
    public void Missing_v1_hop_unavailable_logged_once_until_ForgetMissingHops() // [EDGE-tbse-uvmap-11]
    {
        File.Delete(Path.Combine(dir, "uvmaps", "gen3_to_bibo_transfer.tif"));
        var uv = Service();
        var route = new MensRoute("gen3", "tbse");
        Assert.False(uv.MensRouteAvailable(route));   // files only: no load, no warning
        log.DidNotReceiveWithAnyArgs().Warning(default(string)!, default(object[])!);
        Assert.True(uv.MensRouteAvailable(new MensRoute("bibo", "tbse")));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-12")]
    public void Mirrored_destination_crops_right_half() // [EDGE-tbse-uvmap-12]
    {
        var got = Service().ConvertSheet(Dot(S, S, 12, 2), S, S, "bibo", UVRemapService.MaleVanillaSpace, S)!.Value;
        Assert.Equal(S / 2, got.W);
        // bibo x 12 → TBSE x 3, on the left half: not on the men's vanilla sheet at all.
        Assert.True(got.Rgba.All(b => b == 0));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-15")]
    public void RequestMenMaps_raises_once_per_session() // [EDGE-tbse-uvmap-15]
    {
        var uv = Service();
        int asked = 0;
        uv.MenMapsWanted += () => asked++;
        for (int i = 0; i < 5; i++) uv.RequestMenMaps();
        Assert.Equal(1, asked);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-16")]
    public void Corrupt_map_miss_cached_and_cleared_on_ForgetMissingHops() // [EDGE-tbse-uvmap-16]
    {
        var uv = Service();
        var route = new MensRoute("bibo", "tbse");
        Assert.True(uv.MensRouteAvailable(route));
        uv.MarkRouteMissing(route);
        Assert.False(uv.MensRouteAvailable(route));
        uv.ForgetMissingHops();
        Assert.True(uv.MensRouteAvailable(route));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-4")]
    public void Route_converter_carries_male_vanilla_vertices_through_the_men_map() // [AC-tbse-uvmap-4]
    {
        // A men's vanilla u of 0.25 sits at TBSE u 0.625, pixel 9; bibo_to_tbse mirrors it to bibo pixel 6.
        var conv = Service().RouteConverter(UVRemapService.MaleVanillaSpace, "bibo", unmirror: true)!;
        var got = conv(0.25f, 5f / (S - 1), 1)!.Value;
        Assert.Equal(6f / (S - 1), got.U, 3);
        // gen3 is two maps away: the converter chains them.
        Assert.NotNull(Service().RouteConverter(UVRemapService.MaleVanillaSpace, "gen3", unmirror: true));
        Assert.Null(Service().UvConverter("gen3", "tbse"));
    }
}
