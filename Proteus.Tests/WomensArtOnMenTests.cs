using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>Women's overlays on a man: carried into his layout through the men's maps, held without them, never pasted in hers.</summary>
public class WomensArtOnMenTests(ITestOutputHelper output)
{
    private const string ManB    = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl";
    private const string ManA    = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl";
    private const string HighB   = "chara/human/c0301/obj/body/b0001/material/v0001/mt_c0301b0001_b.mtrl";
    private const string WomanBibo = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
    private const string WomanB  = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_b.mtrl";
    private const string WomanA  = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
    private const string ManFace = "chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_fac_a.mtrl";
    private const string WomanFace = "chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl";

    private static readonly string[] SixWomen =
    [
        WomanBibo,
        "chara/human/c0401/obj/body/b0001/material/v0001/mt_c0401b0001_bibo.mtrl",
        "chara/human/c0601/obj/body/b0001/material/v0001/mt_c0601b0001_bibo.mtrl",
        "chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_bibo.mtrl",
        "chara/human/c1801/obj/body/b0001/material/v0001/mt_c1801b0001_bibo.mtrl",
        "chara/human/c1601/obj/body/b0001/material/v0001/mt_c1601b0001_bibo.mtrl",
    ];

    private static (OverlayEntry Entry, ResolvedOverlay Overlay) Pair(string mod, string? declared, params string[] paths)
        => Pair(mod, OverlayLayer.Skin, declared, paths);

    private static (OverlayEntry Entry, ResolvedOverlay Overlay) Pair(string mod, OverlayLayer layer, string? declared, params string[] paths)
        => (new OverlayEntry(mod, mod, 0, true, new ProteusMetadata(), $"C:/mods/{mod}/Proteus"),
            new ResolvedOverlay(new OverlayDescriptor { Layer = layer, SourceBodyType = declared, MaterialGamePaths = [.. paths], Diffuse = "d.png" },
                                null, "Fabric", mod));

    private static Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> Empty()
        => new(StringComparer.OrdinalIgnoreCase);

    private static WomensArtOnMen.SkinPlan Plan(
        Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial,
        IEnumerable<(OverlayEntry Entry, ResolvedOverlay Overlay)> all,
        string[] active,
        bool man = true, bool settled = true, bool maps = true,
        Func<string, bool>? vanilla = null, Func<string, bool>? men = null, Func<MensRoute, bool>? available = null,
        IEnumerable<(OverlayEntry Entry, ResolvedOverlay Overlay)>? gear = null)
    {
        var allList = all.ToList();
        return WomensArtOnMen.Plan(byMaterial, allList, gear ?? allList.Where(p => p.Overlay.Descriptor.Layer == OverlayLayer.Gear),
            active, man, settled, maps, vanilla ?? (_ => true), men ?? (_ => true), available ?? (_ => true),
            SecondSkinService.ShellLegacySourceOf);
    }

    private static Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> Applied(
        Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial, WomensArtOnMen.SkinPlan plan)
    {
        WomensArtOnMen.Apply(byMaterial, plan);
        return byMaterial;
    }

    private static string Source(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Proteus", file);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException(file);
    }

    // ── carried onto his material ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-1")]
    public void Bibo_only_womens_overlay_reaches_his_tbse_material() // [AC-womens-overlays-men-1]
    {
        var stockings = Pair("Stockings", null, WomanBibo);
        var plan = Plan(Empty(), [stockings], [ManB]);
        var (m, route, pair) = Assert.Single(plan.Carried);
        Assert.Equal(ManB, m);
        Assert.Equal(new MensRoute("bibo", "tbse"), route);
        Assert.Same(stockings.Overlay, pair.Overlay);
        Assert.Same(stockings.Overlay, Assert.Single(Applied(Empty(), plan)[ManB]).Overlay);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-2")]
    public void His_vanilla_material_follows_the_vanilla_toggle() // [AC-womens-overlays-men-2]
    {
        var art = Pair("Stockings", "bibo", WomanBibo);
        var on = Plan(Empty(), [art], [ManB, ManA]);
        Assert.Contains(on.Carried, c => c.Material == ManA && c.Route == new MensRoute("bibo", UVRemapService.MaleVanillaSpace));
        var off = Plan(Empty(), [art], [ManB, ManA], vanilla: _ => false);
        Assert.Equal([ManB], off.Carried.Select(c => c.Material));
        // A men's-vanilla man with vanilla off: nothing to wait for — no map request, no waiting message.
        var vanillaOnly = Plan(Empty(), [art], [ManA], maps: false, vanilla: _ => false);
        Assert.False(vanillaOnly.MapsWanted);
        Assert.Empty(vanillaOnly.AwaitingMods);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-3")]
    public void Undeclared_womens_gen3_pick_routes_gen3_to_tbse() // [AC-womens-overlays-men-3]
    {
        var plan = Plan(Empty(), [Pair("Create", null, WomanB)], [ManB]);
        Assert.Equal(new MensRoute("gen3", "tbse"), Assert.Single(plan.Carried).Route);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-4")]
    public void Maps_absent_holds_skin_and_asks_for_maps() // [AC-womens-overlays-men-4]
    {
        var art = Pair("Stockings", null, WomanBibo);
        var already = Pair("Import", "bibo", ManB, WomanBibo);
        var bm = Empty(); bm[ManB] = [already];
        var plan = Plan(bm, [art, already], [ManB], maps: false);
        Assert.Empty(plan.Carried);
        Assert.True(plan.MapsWanted);
        Assert.Equal(["Import", "Stockings"], plan.AwaitingMods.OrderBy(m => m));
        Assert.Empty(Applied(bm, plan)[ManB]);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    public void Opted_out_mod_reaches_no_man_on_skin_or_shell() // [AC-womens-overlays-men-5]
    {
        var skin = Pair("Stockings", null, WomanBibo);
        var gear = Pair("Stockings", OverlayLayer.Gear, null, WomanBibo);
        var plan = Plan(Empty(), [skin, gear], [ManB], men: m => m != "Stockings");
        Assert.Empty(plan.Carried);
        Assert.False(plan.MapsWanted);
        Assert.Contains(gear.Overlay, plan.HeldGear);
        Assert.DoesNotContain("Stockings", plan.AwaitingMods);   // the switch is not "waiting"

        var (kept, held) = WomensArtOnMen.HoldShellLayers([gear], UVRemapService.TbseSpace, true, true,
            m => m != "Stockings", _ => true, SecondSkinService.ShellLegacySourceOf);
        Assert.Empty(kept);
        Assert.Equal(WomensArtOnMen.MenVerdict.OptedOut, Assert.Single(held).Verdict);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    public void Held_or_opted_out_mods_cast_no_ao_on_him() // [AC-womens-overlays-men-5]
    {
        var gear = Pair("Stockings", OverlayLayer.Gear, null, WomanBibo);
        var his = Pair("Mine", OverlayLayer.Gear, "gen3", ManB);
        var plan = Plan(Empty(), [gear, his], [ManB], maps: false);
        Assert.Equal(["Stockings"], plan.HeldMods);
        // Switched off for men, or a men's map missing: held too, so no AO either.
        Assert.Equal(["Stockings"], Plan(Empty(), [gear, his], [ManB], men: m => m != "Stockings").HeldMods);
        Assert.Equal(["Stockings"], Plan(Empty(), [gear, his], [ManB], available: _ => false).HeldMods);
        // A unisex pack with something of its own drawn on him keeps its AO.
        var unisex = Plan(Empty(), [Pair("Both", OverlayLayer.Gear, null, WomanBibo), Pair("Both", OverlayLayer.Gear, "gen3", ManB)],
                          [ManB], maps: false);
        Assert.Empty(unisex.HeldMods);

        var cr = Source("Services/CompositorService.CompositeRun.cs");
        Assert.Contains("&& !menAoExcluded.Contains(e.ModDirectory)", cr);
        Assert.Contains("menAoExcluded = womensPlan.HeldMods;", cr);
        Assert.Contains("material.dstLayout != null && material.run.menAoExcluded.Contains(modDir)",
                        Source("Services/CompositorService.OcclusionPass.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    public void Opted_out_held_even_without_men_layout() // [AC-womens-overlays-men-5]
    {
        var gear = Pair("Stockings", OverlayLayer.Gear, null, WomanBibo);
        // Parts that don't agree on one men's layout: switched off or waiting is still held; a missing map is left to the shell.
        var off = WomensArtOnMen.HoldShellLayers([gear], null, true, true, _ => false, _ => true, SecondSkinService.ShellLegacySourceOf);
        Assert.Single(off.Held);
        var waiting = WomensArtOnMen.HoldShellLayers([gear], null, true, false, _ => true, _ => true, SecondSkinService.ShellLegacySourceOf);
        Assert.Single(waiting.Held);
        var drawn = WomensArtOnMen.HoldShellLayers([gear], null, true, true, _ => true, _ => true, SecondSkinService.ShellLegacySourceOf);
        Assert.Single(drawn.Kept);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-6")]
    public void His_own_art_is_neither_carried_nor_held() // [AC-womens-overlays-men-6]
    {
        var his = Pair("Mine", "gen3", ManB);
        var bm = Empty(); bm[ManB] = [his];
        var plan = Plan(bm, [his], [ManB], maps: false, men: _ => false);
        Assert.Empty(plan.Carried);
        Assert.Empty(plan.Removed);
        Assert.False(plan.MapsWanted);
        Assert.Same(his.Overlay, Assert.Single(Applied(bm, plan)[ManB]).Overlay);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-6")]
    public void Overlay_already_on_his_material_is_not_doubled() // [AC-womens-overlays-men-6]
    {
        var import = Pair("Import", "bibo", ManB, WomanBibo);
        var bm = Empty(); bm[ManB] = [import];
        var plan = Plan(bm, [import], [ManB]);
        Assert.Empty(plan.Carried);
        Assert.Single(Applied(bm, plan)[ManB]);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-6")]
    public void Unisex_pack_does_not_double() // [AC-womens-overlays-men-6]
    {
        var hers = Pair("Both", null, WomanBibo);
        var his = Pair("Both", "gen3", ManB);
        var bm = Empty(); bm[ManB] = [his];
        var plan = Plan(bm, [hers, his], [ManB]);
        Assert.Empty(plan.Carried);
        // Another mod's art on his material does not stop hers.
        var bm2 = Empty(); bm2[ManB] = [Pair("Other", "gen3", ManB)];
        Assert.Single(Plan(bm2, [hers], [ManB]).Carried);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-7")]
    public void Not_a_man_changes_nothing() // [AC-womens-overlays-men-7]
    {
        var art = Pair("Stockings", null, WomanBibo);
        var bm = Empty(); bm[WomanBibo] = [art];
        var plan = Plan(bm, [art, Pair("S", OverlayLayer.Gear, null, WomanBibo)], [WomanBibo, ManB], man: false, maps: false);
        Assert.Same(WomensArtOnMen.SkinPlan.Empty, plan);
        var layers = new[] { Pair("S", OverlayLayer.Gear, null, WomanBibo) };
        var (kept, held) = WomensArtOnMen.HoldShellLayers(layers, null, false, false, _ => false, _ => false, SecondSkinService.ShellLegacySourceOf);
        Assert.Single(kept);
        Assert.Empty(held);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-7")]
    [Trait("Plan", "EDGE-womens-overlays-men-22")]
    public void Mask_shell_paths_empty_for_women_and_his_own_art() // [AC-womens-overlays-men-7] [EDGE-womens-overlays-men-22]
    {
        Assert.Empty(WomensArtOnMen.MaskShellPathsFor(Pair("S", null, WomanBibo).Overlay.Descriptor, wearerIsMan: false));
        Assert.Empty(WomensArtOnMen.MaskShellPathsFor(Pair("S", "gen3", ManB).Overlay.Descriptor, wearerIsMan: true));
        Assert.Empty(WomensArtOnMen.MaskShellPathsFor(Pair("S", "gen3", WomanB).Overlay.Descriptor, wearerIsMan: true));
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-8")]
    public void Womens_only_gear_layer_takes_his_route_on_the_shell() // [AC-womens-overlays-men-8]
    {
        var sheer = Pair("Stockings", OverlayLayer.Gear, null, SixWomen);
        var (kept, held) = WomensArtOnMen.HoldShellLayers([sheer], UVRemapService.TbseSpace, true, true, _ => true, _ => true,
                                                          SecondSkinService.ShellLegacySourceOf);
        Assert.Single(kept);
        Assert.Empty(held);
        Assert.Equal(new MensRoute("bibo", "tbse"),
            UVRemapService.MensRouteFor(null, SecondSkinService.ShellLegacySourceOf(sheer.Overlay.Descriptor),
                                        sheer.Overlay.Descriptor.MaterialGamePaths, UVRemapService.TbseSpace));
        Assert.Empty(Plan(Empty(), [sheer], [ManB]).Carried);   // gear is the shell's, never byMaterial's
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-8")]
    [Trait("Plan", "EDGE-womens-overlays-men-22")]
    public void Mask_shell_seeded_from_womens_art_carries_seed_paths_on_a_man() // [AC-womens-overlays-men-8] [EDGE-womens-overlays-men-22]
    {
        var seed = Pair("Stockings", OverlayLayer.Gear, null, SixWomen).Overlay.Descriptor;
        Assert.Equal(SixWomen, WomensArtOnMen.MaskShellPathsFor(seed, wearerIsMan: true));
        var mask = new OverlayDescriptor { IsMaskShell = true, Layer = OverlayLayer.Gear, MaterialGamePaths = WomensArtOnMen.MaskShellPathsFor(seed, true) };
        Assert.True(WomensArtOnMen.IsBodyShellLayer(mask));
        Assert.Equal("bibo", SecondSkinService.ShellLegacySourceOf(mask));
        Assert.Contains("MaterialGamePaths = WomensArtOnMen.MaskShellPathsFor(seed.Overlay.Descriptor, run.wearerIsMan)",
                        Source("Services/CompositorService.ShellPhase.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-9")]
    public void Carried_mod_is_in_a_material_list() // [AC-womens-overlays-men-9]
    {
        var bm = Applied(Empty(), Plan(Empty(), [Pair("Stockings", null, WomanBibo)], [ManB]));
        Assert.Contains(bm.Values.SelectMany(l => l), p => p.Entry.ModDirectory == "Stockings");
        // ExplainInertMods counts a mod as contributing off byMaterial's lists, after the carry.
        Assert.Contains("Stockings", InertModDiagnosis.Contributing(bm, [], [], []));
        Assert.Contains("var contributing = Contributing(byMaterial, gearOverlays, contentLayers, maskShellMods, awaitingMenMaps, heldGear);",
                        Source("Services/CompositorService.Upstream.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-9")]
    public void Summary_is_one_line_and_null_when_nothing_happened() // [AC-womens-overlays-men-9]
    {
        Assert.Null(WomensArtOnMen.Summary(WomensArtOnMen.SkinPlan.Empty));
        Assert.Null(WomensArtOnMen.Summary(Plan(Empty(), [Pair("Mine", "gen3", ManB)], [ManB])));
        var s = WomensArtOnMen.Summary(Plan(Empty(), [Pair("Stockings", null, WomanBibo)], [ManB, ManA]));
        Assert.NotNull(s);
        Assert.DoesNotContain('\n', s);
        Assert.Contains("carried 2 overlay(s) onto 2 body material(s) [Stockings]", s);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-9")]
    public void Summary_logged_only_on_change() // [AC-womens-overlays-men-9]
    {
        var cr = Source("Services/CompositorService.CompositeRun.cs");
        Assert.Contains("if (!string.Equals(summary, compositor._lastWomensArtSummary, StringComparison.Ordinal))", cr);
        Assert.Contains("if (summary != null) compositor.log.Information(summary);", cr);
    }

    // ── edges ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-1")]
    public void Lalafell_male_hrothgar_unknown_and_woman_get_nothing() // [EDGE-womens-overlays-men-1]
    {
        var art = Pair("Stockings", null, WomanBibo);
        // Lalafell (c1101) and male Hrothgar (c1501) skin is no men's layout: not a destination.
        foreach (var code in new[] { "c1101", "c1501", "c0201" })
            Assert.Empty(Plan(Empty(), [art], [$"chara/human/{code}/obj/body/b0001/material/v0001/mt_{code}b0001_b.mtrl"]).Carried);
        Assert.False(UVRemapService.WearerIsMan(null, ["c1101"], null));
        Assert.False(UVRemapService.WearerIsMan(null, [], null));
        Assert.False(UVRemapService.WearerIsMan(null, ["c0101"], ["c0201"]));
        Assert.False(UVRemapService.WearerIsMan("c0201", ["c0101"], null));
        Assert.Same(WomensArtOnMen.SkinPlan.Empty, Plan(Empty(), [art], [ManB], man: false, maps: false));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-2")]
    public void Glamourer_override_carries_nothing_until_settled() // [EDGE-womens-overlays-men-2]
    {
        Assert.Empty(Plan(Empty(), [Pair("Stockings", null, WomanBibo)], [ManB], settled: false).Carried);
        Assert.Contains("raceUnsettled = glamOverride;", Source("Services/CompositorService.CompositeRun.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-3")]
    public void No_bare_skin_carries_nothing() // [EDGE-womens-overlays-men-3]
    {
        Assert.Empty(Plan(Empty(), [Pair("Stockings", null, WomanBibo)], [ManFace]).Carried);
        Assert.Empty(WomensArtOnMen.Plan(Empty(), [Pair("Stockings", null, WomanBibo)], [], null, true, true, true,
            _ => true, _ => true, _ => true, SecondSkinService.ShellLegacySourceOf).Carried);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-4")]
    public void Six_womens_paths_make_one_pair_per_destination() // [EDGE-womens-overlays-men-4]
    {
        var plan = Plan(Empty(), [Pair("Stockings", null, SixWomen)], [ManB, ManA]);
        Assert.Equal(2, plan.Carried.Count);
        Assert.Equal([ManA, ManB], plan.Carried.Select(c => c.Material).OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-5")]
    public void Sibling_key_is_not_doubled() // [EDGE-womens-overlays-men-5]
    {
        var art = Pair("Stockings", null, WomanBibo);
        var bm = Empty(); bm[ManA] = [art];   // as a sibling would have put it
        var plan = Plan(bm, [art], [ManB, ManA]);
        Assert.Equal([ManB], plan.Carried.Select(c => c.Material));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-5")]
    [Trait("Plan", "EDGE-womens-overlays-men-9")]
    public void Carried_pair_is_reference_equal_to_its_source() // [EDGE-womens-overlays-men-5] [EDGE-womens-overlays-men-9]
    {
        var art = Pair("Stockings", null, WomanBibo);
        var got = Assert.Single(Applied(Empty(), Plan(Empty(), [art], [ManB]))[ManB]);
        Assert.Same(art.Entry, got.Entry);
        Assert.Same(art.Overlay, got.Overlay);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-6")]
    public void Undeclared_womens_vanilla_has_no_route() // [EDGE-womens-overlays-men-6]
    {
        var art = Pair("Onion", null, WomanA);
        Assert.Null(WomensArtOnMen.SkinLegacySourceOf(art.Overlay.Descriptor));
        var plan = Plan(Empty(), [art], [ManB, ManA], maps: false);
        Assert.Empty(plan.Carried);
        Assert.False(plan.MapsWanted);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-7")]
    public void Route_missing_holds_skin_and_shell_without_asking() // [EDGE-womens-overlays-men-7]
    {
        var art = Pair("Stockings", null, WomanBibo);
        var gear = Pair("Stockings", OverlayLayer.Gear, null, WomanBibo);
        var plan = Plan(Empty(), [art, gear], [ManB], available: _ => false);
        Assert.Empty(plan.Carried);
        Assert.False(plan.MapsWanted);
        Assert.Contains(gear.Overlay, plan.HeldGear);
        var (_, held) = WomensArtOnMen.HoldShellLayers([gear], UVRemapService.TbseSpace, true, true, _ => true, _ => false,
                                                       SecondSkinService.ShellLegacySourceOf);
        Assert.Equal(WomensArtOnMen.MenVerdict.RouteMissing, Assert.Single(held).Verdict);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-8")]
    public void Only_mens_body_skin_is_a_destination() // [EDGE-womens-overlays-men-8]
    {
        string[] active = [ManB, ManFace, WomanBibo, "chara/equipment/e0100/material/v0001/mt_c0101e0100_top_a.mtrl",
                           "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_bibo.mtrl"];
        Assert.Equal([ManB], Plan(Empty(), [Pair("Stockings", null, WomanBibo)], active).Carried.Select(c => c.Material));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-10")]
    public void His_pair_is_untouched_beside_a_carried_one() // [EDGE-womens-overlays-men-10]
    {
        var his = Pair("Mine", "gen3", ManB);
        var hers = Pair("Stockings", null, WomanBibo);
        var bm = Empty(); bm[ManB] = [his];
        var list = Applied(bm, Plan(bm, [his, hers], [ManB]))[ManB];
        Assert.Equal(2, list.Count);
        Assert.Same(his.Overlay, list[0].Overlay);
        Assert.Same(hers.Overlay, list[1].Overlay);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-11")]
    public void Other_mens_races_receive_it() // [EDGE-womens-overlays-men-11]
    {
        Assert.Equal([HighB], Plan(Empty(), [Pair("Stockings", null, WomanBibo)], [HighB]).Carried.Select(c => c.Material));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-12")]
    public void Declared_gen3_or_gen2_womens_only_is_neither_carried_nor_held() // [EDGE-womens-overlays-men-12]
    {
        foreach (var t in new[] { "gen3", "gen2" })
        {
            var art = Pair("Luminis", t, WomanB);
            var plan = Plan(Empty(), [art], [ManB], maps: false);
            Assert.Empty(plan.Carried);
            Assert.False(plan.MapsWanted);
            Assert.Single(WomensArtOnMen.HoldShellLayers([Pair("L", OverlayLayer.Gear, t, WomanB)], UVRemapService.TbseSpace, true,
                false, _ => false, _ => false, SecondSkinService.ShellLegacySourceOf).Kept);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-13")]
    public void Carry_runs_after_synthesis_and_before_outputs() // [EDGE-womens-overlays-men-13]
    {
        var cr = Source("Services/CompositorService.CompositeRun.cs");
        int syn = cr.IndexOf("if (!SynthesiseSiblings()) return;", StringComparison.Ordinal);
        int carry = cr.IndexOf("CarryWomensArtToMen();", StringComparison.Ordinal);
        int prep = cr.IndexOf("PrepareOutputs();", StringComparison.Ordinal);
        int gate = cr.IndexOf("if (!GateUnchanged()) return;", StringComparison.Ordinal);
        Assert.True(syn >= 0 && syn < carry && carry < prep && prep < gate);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-14")]
    public void Maps_wanted_only_on_await_verdict() // [EDGE-womens-overlays-men-14]
    {
        var art = Pair("Stockings", null, WomanBibo);
        Assert.True(Plan(Empty(), [art], [ManB], maps: false).MapsWanted);
        Assert.False(Plan(Empty(), [art], [ManB]).MapsWanted);
        Assert.False(Plan(Empty(), [art], [ManB], maps: false, men: _ => false).MapsWanted);
        Assert.False(Plan(Empty(), [Pair("Mine", "gen3", ManB)], [ManB], maps: false).MapsWanted);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-15")]
    public void Gear_layer_is_never_carried_demoted_skin_is() // [EDGE-womens-overlays-men-15]
    {
        Assert.Empty(Plan(Empty(), [Pair("S", OverlayLayer.Gear, null, WomanBibo)], [ManB]).Carried);
        Assert.Single(Plan(Empty(), [Pair("S", OverlayLayer.Skin, null, WomanBibo)], [ManB]).Carried);
    }

    [Theory]
    [Trait("Plan", "EDGE-womens-overlays-men-15")]
    [InlineData("bibo", new[] { WomanB }, "bibo")]
    [InlineData(null, new[] { WomanB, WomanBibo }, "bibo")]
    [InlineData(null, new[] { WomanB }, "gen3")]
    [InlineData(null, new[] { WomanA }, null)]
    [InlineData(null, new string[0], null)]
    public void SkinLegacySourceOf_reads_like_SrcTypeOf(string? declared, string[] paths, string? expected) // [EDGE-womens-overlays-men-15]
    {
        var d = new OverlayDescriptor { SourceBodyType = declared, MaterialGamePaths = [.. paths] };
        Assert.Equal(expected, WomensArtOnMen.SkinLegacySourceOf(d));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-16")]
    public void Declared_bibo_without_body_skin_path_is_not_carried() // [EDGE-womens-overlays-men-16]
    {
        Assert.Empty(Plan(Empty(), [Pair("Makeup", "bibo", WomanFace)], [ManB]).Carried);
        Assert.Empty(Plan(Empty(), [Pair("NoPaths", "bibo")], [ManB]).Carried);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-17")]
    public void All_held_shell_lets_the_carrier_sweep_run() // [EDGE-womens-overlays-men-17]
    {
        var (kept, held) = WomensArtOnMen.HoldShellLayers([Pair("S", OverlayLayer.Gear, null, WomanBibo), Pair("T", OverlayLayer.Gear, "bibo", WomanBibo)],
            UVRemapService.TbseSpace, true, false, _ => true, _ => true, SecondSkinService.ShellLegacySourceOf);
        Assert.Empty(kept);
        Assert.Equal(2, held.Count);
        var cr = Source("Services/CompositorService.CompositeRun.cs");
        Assert.Contains("bool hostWanted = (gearOverlays.Count > 0 && !shellAllHeld) || contentLayers.Count > 0;", cr);
        Assert.Contains("run.shellAllHeld = run.compositor.secondSkin.LastBuildHeldAll;", Source("Services/CompositorService.ShellPhase.cs"));
        var sb = Source("Services/SecondSkinService.ShellSetBuild.cs");
        Assert.Contains("if (kept.Count == 0) service.LastBuildHeldAll = true;", sb);
        Assert.Contains("service.LastBuildHeldAll = false;", sb);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-21")]
    public void Opt_out_read_from_a_gate_snapshot() // [EDGE-womens-overlays-men-21]
    {
        var cr = Source("Services/CompositorService.CompositeRun.cs").Replace("\r\n", "\n");
        Assert.Contains("lock (compositor.config.Gate)\n                menOffMods = new HashSet<string>(compositor.config.MenOverlayOffMods, StringComparer.OrdinalIgnoreCase);", cr);
        Assert.DoesNotContain("config.OverlaysMenFor", cr);
        var sw = Source("Gui/StatusWindow.cs").Replace("\r\n", "\n");
        Assert.Contains("lock (config.Gate)\n            {\n                if (men) config.MenOverlayOffMods.Remove(entry.ModDirectory);", sw);
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    [Trait("Plan", "EDGE-womens-overlays-men-20")]
    public void Bodies_panel_draws_men_checkbox_and_recomposites() // [AC-womens-overlays-men-5] [EDGE-womens-overlays-men-20]
    {
        var sw = Source("Gui/StatusWindow.cs");
        int vanilla = sw.IndexOf("{cp.OverlayVanilla}##bodies_", StringComparison.Ordinal);
        int men = sw.IndexOf("{cp.OverlayMen}##men_{entry.ModDirectory}", StringComparison.Ordinal);
        Assert.True(vanilla >= 0 && men > vanilla);
        Assert.Contains("RecompositeForOverlay(entry, \"men-mode\");", sw);
        Assert.Contains("ImGui.SetTooltip(cp.MenTip + (binding ? cp.BodiesGlobalSuffix : \"\"));", sw);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-23")]
    public void Held_gear_is_predicted_and_reported() // [EDGE-womens-overlays-men-23]
    {
        var gear = Pair("Stockings", OverlayLayer.Gear, null, WomanBibo);
        var his = Pair("Mine", OverlayLayer.Gear, "gen3", ManB);
        var face = Pair("Makeup", OverlayLayer.Gear, "bibo", WomanFace);
        var plan = Plan(Empty(), [gear, his, face], [ManB], maps: false);
        Assert.Contains(gear.Overlay, plan.HeldGear);
        Assert.DoesNotContain(his.Overlay, plan.HeldGear);
        Assert.DoesNotContain(face.Overlay, plan.HeldGear);
        Assert.Contains("Stockings", plan.AwaitingMods);
        Assert.Contains("womensPlan.AwaitingMods, womensPlan.HeldGear", Source("Services/CompositorService.CompositeRun.cs"));

        // The diagnosis: a held gear layer and the mask shell of a mod waiting for the maps reach nothing; his own do.
        var contributing = InertModDiagnosis.Contributing(Empty(), [gear, his, face], [], ["Stockings", "Mine"],
                                                          plan.AwaitingMods, plan.HeldGear);
        Assert.DoesNotContain("Stockings", contributing);
        Assert.Contains("Mine", contributing);
        Assert.Contains("Makeup", contributing);
        // Without the women's plan (a woman, or nothing held) everything counts as before.
        Assert.Contains("Stockings", InertModDiagnosis.Contributing(Empty(), [gear], [], ["Stockings"]));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-24")]
    public void Maps_ready_is_taken_once_per_run() // [EDGE-womens-overlays-men-24]
    {
        var cr = Source("Services/CompositorService.CompositeRun.cs");
        Assert.Single(Regex.Matches(cr, @"menMapsReady = compositor\.uvRemap\.MenMapsPresent\(\);"));
        Assert.Contains("menMapsReady, compositor.config.OverlaysVanillaFor, OverlaysMenFor", cr);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-25")]
    public void Pair_already_listing_his_race_is_held_unless_draw() // [EDGE-womens-overlays-men-25]
    {
        foreach (var (maps, allowed, avail, keep) in new[] { (false, true, true, false), (true, false, true, false), (true, true, false, false), (true, true, true, true) })
        {
            var import = Pair("Import", "bibo", ManB, WomanBibo);
            var bm = Empty(); bm[ManB] = [import];
            var list = Applied(bm, Plan(bm, [import], [ManB], maps: maps, men: _ => allowed, available: _ => avail))[ManB];
            Assert.Equal(keep, list.Count == 1);
        }
    }

    // ── the real pack and its cost ───────────────────────────────────────────────────────────

    private const string StockingsMeta = LocalData.Mods + ":Solona's Stockings - for Proteus/Proteus/metadata.json";

    [LocalDataFact(StockingsMeta)]
    [Trait("Plan", "AC-womens-overlays-men-1")]
    public void Real_stockings_pack_reaches_a_tbse_midlander() // [AC-womens-overlays-men-1]
    {
        var meta = JsonSerializer.Deserialize<ProteusMetadata>(File.ReadAllText(LocalData.Path(StockingsMeta)))!;
        var descs = (meta.Overlays ?? []).Concat((meta.OptionGroups ?? []).SelectMany(g => g.Options).SelectMany(o => o.Overlays)).ToList();
        var entry = new OverlayEntry("Solona's Stockings - for Proteus", "Stockings", 0, true, meta, "C:/x/Proteus");
        var pairs = descs.Select(d => (entry, new ResolvedOverlay(d, null, null, null))).ToList();
        var skin = pairs.Where(p => p.Item2.Descriptor.Layer != OverlayLayer.Gear).ToList();
        var gear = pairs.Where(p => p.Item2.Descriptor.Layer == OverlayLayer.Gear).ToList();
        output.WriteLine($"{skin.Count} skin, {gear.Count} gear descriptor(s)");
        Assert.NotEmpty(skin);
        Assert.NotEmpty(gear);

        var plan = Plan(Empty(), pairs, [ManB]);
        Assert.Equal(skin.Count, plan.Carried.Count);
        Assert.All(plan.Carried, c => Assert.Equal(new MensRoute("bibo", "tbse"), c.Route));
        var (kept, held) = WomensArtOnMen.HoldShellLayers(gear, UVRemapService.TbseSpace, true, true, _ => true, _ => true,
                                                          SecondSkinService.ShellLegacySourceOf);
        Assert.Equal(gear.Count, kept.Count);
        Assert.Empty(held);
    }
}
