using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Interop;
using Proteus.Services;
using Xunit;
using static Proteus.Services.DesignRestoreSweep;

namespace Proteus.Tests;

/// <summary>
/// The design-restore sweep held off "clia skin" — the only supplier of <c>chara/bibo_mid_*.tex</c>, which the kept
/// YAB+ body material needs and the game doesn't ship — and the body went invisible. These pin what it now decides:
/// a stray that supplies a game-less file something kept needs stays on; every other stray is still held off.
/// </summary>
public class DesignRestoreSweepTests
{
    private const string Root = "C:/Mods";
    private const string Managed = SidecarDiscoveryService.ManagedModDir;
    private const string Yab = "hs-Yet Another Body+-4.2.0-tmi";
    private const string Clia = "clia skin - by solona";
    private const string Stryx = "LM Stryx Rainbow";

    private const string BiboMtrl = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
    private const string BiboBase = "chara/bibo_mid_base.tex";
    private const string BiboNorm = "chara/bibo_mid_norm.tex";
    private const string BodyModel = "chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string HairTex = "chara/human/c0201/obj/hair/h0168/texture/c0201h0168_hir_norm.tex";

    private static string Full(string dir, string game, string tag = "") => $"{Root}/{dir}/files{tag}/{game}";

    private static ModFile F(string dir, string game, string[]? refs = null, bool def = true, string tag = "", bool bad = false)
        => new(NormGame(game), Full(dir, game, tag), def, refs ?? [], bad);

    private static ModDirFiles M(params ModFile[] files) => new(false, files);

    private static Dictionary<string, ModDirFiles> D(params (string Dir, ModDirFiles Files)[] e)
        => e.ToDictionary(x => x.Dir, x => x.Files, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Shipped = new(StringComparer.Ordinal) { HairTex, "chara/common/texture/skin_m.tex" };

    /// <summary>Inputs whose resolution is "each game path resolves to the file of the mod listed for it".</summary>
    private static SweepInputs In(
        Dictionary<string, ModDirFiles>? kept = null, Dictionary<string, ModDirFiles>? candidates = null,
        Dictionary<string, ModDirFiles>? held = null, Dictionary<string, string?>? resolved = null,
        List<(string, string)>? upstreams = null, Func<string, bool>? exists = null, HashSet<string>? sanity = null)
        => new(Root, Managed, kept ?? D(), sanity ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
               candidates ?? D(), held ?? D(), resolved ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
               upstreams ?? [], exists ?? Shipped.Contains);

    private static Dictionary<string, string?> R(params (string Game, string? File)[] e)
        => e.ToDictionary(x => NormGame(x.Game), x => x.File, StringComparer.OrdinalIgnoreCase);

    /// <summary>The Chenille case: YAB+'s active Matte material needs clia's game-less textures.</summary>
    private static (Dictionary<string, ModDirFiles> Kept, Dictionary<string, string?> Resolved) MatteCase()
        => (D((Yab, M(F(Yab, BiboMtrl, [BiboBase, BiboNorm], def: false)))),
            R((BiboMtrl, Full(Yab, BiboMtrl)), (BiboBase, Full(Clia, BiboBase)), (BiboNorm, Full(Clia, BiboNorm)),
              (HairTex, Full(Stryx, HairTex))));

    private static Dictionary<string, ModDirFiles> CliaTextures() => D((Clia, M(F(Clia, BiboBase), F(Clia, BiboNorm))));

    // ── AC-1/2: spared vs held ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-1")]
    public void FindProviders_KeptMatteMaterialNeedsGamelessTextures_SparesStray()
    {
        var (kept, resolved) = MatteCase();
        var candidates = CliaTextures();
        candidates[Stryx] = M(F(Stryx, HairTex));
        var p = FindProviders(In(kept, candidates, resolved: resolved));

        Assert.Equal([Clia], p.Keys);
        Assert.Equal([BiboBase, BiboNorm], p[Clia].Select(n => n.GamePath).Order());
        Assert.All(p[Clia], n => Assert.Equal(Yab, n.NeededBy));
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-2")]
    public void FindProviders_StrayReplacingShippedFiles_NotSpared()
    {
        var (kept, resolved) = MatteCase();
        var p = FindProviders(In(kept, CliaTextures(), resolved: resolved, exists: _ => true));
        Assert.Empty(p);   // the game has its own bibo_mid textures here: holding clia off loses nothing
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-2")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-8")]
    public void FindProviders_StrayOwnInventedPaths_NotSpared()
    {
        // The stray's own material (a game path) needs its own invented texture: only the stray needs it.
        const string gearMtrl = "chara/equipment/e6000/material/v0001/mt_c0201e6000_top_a.mtrl";
        const string custom = "chara/custom/stray_tex.tex";
        var candidates = D(("Stray", M(F("Stray", gearMtrl, [custom]), F("Stray", custom))));
        var resolved = R((gearMtrl, Full("Stray", gearMtrl)), (custom, Full("Stray", custom)));
        Assert.Empty(FindProviders(In(D((Yab, M())), candidates, resolved: resolved, exists: g => g == gearMtrl)));
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-2")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-3")]
    public void FindProviders_KeptSkinWinsBiboPaths_StraySkinHeld()
    {
        // The design names skin X; stray skin Y redirects the same bibo_mid paths, but X (raised) wins them.
        var (kept, _) = MatteCase();
        kept["Skin X"] = M(F("Skin X", BiboBase), F("Skin X", BiboNorm));
        var resolved = R((BiboMtrl, Full(Yab, BiboMtrl)), (BiboBase, Full("Skin X", BiboBase)), (BiboNorm, Full("Skin X", BiboNorm)));
        var candidates = D(("Skin Y", M(F("Skin Y", BiboBase), F("Skin Y", BiboNorm))));
        Assert.Empty(FindProviders(In(kept, candidates, resolved: resolved)));
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-3")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-20")]
    public void FindProviders_FollowsChain_AndDeepChainTerminates()
    {
        // kept model → B's game-less material → C's game-less material → D's game-less texture.
        const string matB = "chara/equipment/e9001/material/v0001/mt_c0201e9001_top_b.mtrl";
        const string texC = "chara/custom/c_layer.tex";
        const string matC = "chara/equipment/e9002/material/v0001/mt_c0201e9002_top_c.mtrl";
        const string texD = "chara/custom/d_layer.tex";
        var kept = D(("Kept", M(F("Kept", BodyModel, ["/mt_c0201e9001_top_b.mtrl"]))));
        var candidates = D(("B", M(F("B", matB, [texC]))),
                           ("C", M(F("C", texC), F("C", matC, [texD]))),
                           ("D", M(F("D", texD))));
        // C is needed through B's material; once spared, C's own material makes D needed too.
        var resolved = R((BodyModel, Full("Kept", BodyModel)), (matB, Full("B", matB)), (texC, Full("C", texC)),
                         (matC, Full("C", matC)), (texD, Full("D", texD)));
        var p = FindProviders(In(kept, candidates, resolved: resolved, exists: _ => false));

        Assert.Equal(["B", "C", "D"], p.Keys.Order());
        Assert.Equal("Kept", p["B"].Single().NeededBy);
        Assert.Equal("B", p["C"].Single(n => n.GamePath == texC).NeededBy);
        Assert.Equal("C", p["D"].Single().NeededBy);
    }

    // ── AC-4: a kept model's skin material ──────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-4")]
    public void FindProviders_KeptModelDeclaresStrayGamelessMaterial_SparesStrayAndItsTextures()
    {
        // YAB+ "Skin Materials" Off: the body model is kept, its bibo material comes only from clia.
        var kept = D((Yab, M(F(Yab, BodyModel, ["/mt_c0201b0001_bibo.mtrl"]))));
        var candidates = D((Clia, M(F(Clia, BiboMtrl, [BiboBase, BiboNorm]), F(Clia, BiboBase), F(Clia, BiboNorm))));
        var resolved = R((BodyModel, Full(Yab, BodyModel)), (BiboMtrl, Full(Clia, BiboMtrl)),
                         (BiboBase, Full(Clia, BiboBase)), (BiboNorm, Full(Clia, BiboNorm)));
        var p = FindProviders(In(kept, candidates, resolved: resolved));
        Assert.Equal([Clia], p.Keys);
        Assert.Contains(p[Clia], n => n.GamePath == BiboMtrl && n.NeededBy == Yab);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-4")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-39")]
    public void FindProviders_C1401SkinMaterial_MatchesBySuffix()
    {
        const string vieraMtrl = "chara/human/c1401/obj/body/b0101/material/v0001/mt_c1401b0101_bibo.mtrl";
        var kept = D((Yab, M(F(Yab, BodyModel, ["/mt_c0201b0001_bibo.mtrl"]))));
        var candidates = D(("Viera Skin", M(F("Viera Skin", vieraMtrl))));
        var resolved = R((BodyModel, Full(Yab, BodyModel)), (vieraMtrl, Full("Viera Skin", vieraMtrl)));
        Assert.Equal(["Viera Skin"], FindProviders(In(kept, candidates, resolved: resolved)).Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-39")]
    public void MaterialKey_SkinSuffixWildcard_OtherNamesExact()
    {
        Assert.Equal(MaterialKey("/mt_c0201b0001_bibo.mtrl"), MaterialKey("chara/human/c1401/obj/body/b0101/material/v0001/mt_c1401b0101_bibo.mtrl"));
        Assert.NotEqual(MaterialKey("/mt_c0201b0001_a.mtrl"), MaterialKey("/mt_c0201b0001_bibo.mtrl"));
        Assert.Equal("file:mt_c0201e6255_top_a.mtrl", MaterialKey("/mt_c0201e6255_top_a.mtrl"));
        Assert.NotEqual(MaterialKey("/mt_c0201e6255_top_a.mtrl"), MaterialKey("/mt_c0101e6255_top_a.mtrl"));
    }

    // ── AC-5/6/7 ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-5")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-45")]
    public void FindProviders_NothingDrawnYet_SameAnswer_AndCarriedDirsAreCandidates()
    {
        // Nothing about what is drawn enters the decision: clia, carried by a re-apply and not drawn yet, is judged
        // from the collection and the files alone; a carried non-provider is not spared.
        var (kept, resolved) = MatteCase();
        var candidates = CliaTextures();
        candidates[Stryx] = M(F(Stryx, HairTex));
        var p = FindProviders(In(kept, candidates, resolved: resolved));
        Assert.True(p.ContainsKey(Clia));
        Assert.False(p.ContainsKey(Stryx));
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-6")]
    public void FindProviders_HeldStrayNowNeeded_IsProvider()
    {
        // clia is held off: its paths resolve to nothing, and the kept material still needs them.
        var (kept, _) = MatteCase();
        var resolved = R((BiboMtrl, Full(Yab, BiboMtrl)), (BiboBase, null), (BiboNorm, BiboNorm));
        Assert.Equal([Clia], FindProviders(In(kept, held: CliaTextures(), resolved: resolved)).Keys);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-7")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-6")]
    public void FindProviders_UpstreamGamelessBaseFromStray_Spares_ManagedWinsUpstreamDecides()
    {
        // The composite publishes bibo_mid_base under the managed mod; its upstream is clia's file.
        var resolved = R((BiboBase, Full(Managed, BiboBase)));
        var withUpstream = FindProviders(In(D((Managed, M())), CliaTextures(), resolved: resolved,
                                            upstreams: [(BiboBase, Clia)]));
        Assert.Equal(CompositeNeed, withUpstream[Clia].Single().NeededBy);

        var none = FindProviders(In(D((Managed, M())), CliaTextures(), resolved: resolved));
        Assert.Empty(none);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-23")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-24")]
    public void FindProviders_UpstreamFullPathKeptSourceOrShippedPath_Ignored()
    {
        Assert.Empty(FindProviders(In(D((Yab, M())), CliaTextures(), upstreams: [(BiboBase, "E:/Other/clia/x.tex"), (BiboBase, Yab)])));
        Assert.Empty(FindProviders(In(D((Yab, M())), CliaTextures(), upstreams: [(HairTex, Clia)])));
    }

    // ── edges ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-1")]
    public void FindProviders_FileExistsThrows_IsAbsent()
    {
        var (kept, resolved) = MatteCase();
        var p = FindProviders(In(kept, CliaTextures(), resolved: resolved, exists: _ => throw new InvalidOperationException()));
        Assert.True(p.ContainsKey(Clia));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-2")]
    public void FindProviders_CandidatePathUnresolved_NotProviderViaIt()
    {
        var (kept, _) = MatteCase();
        var resolved = R((BiboMtrl, Full(Yab, BiboMtrl)), (BiboBase, null), (BiboNorm, BiboNorm));
        Assert.Empty(FindProviders(In(kept, CliaTextures(), resolved: resolved)));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-4")]
    public void FindProviders_StrayOutranksKeptRedirect_Spared()
    {
        var (kept, resolved) = MatteCase();
        kept["Skin X"] = M(F("Skin X", BiboBase));   // also redirects it, but clia wins the path
        Assert.True(FindProviders(In(kept, CliaTextures(), resolved: resolved)).ContainsKey(Clia));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-5")]
    public void FindProviders_OtherStrayWins_OnlyWinnerJudged()
    {
        var (kept, _) = MatteCase();
        var candidates = CliaTextures();
        candidates["Other Skin"] = M(F("Other Skin", BiboBase), F("Other Skin", BiboNorm));
        var resolved = R((BiboMtrl, Full(Yab, BiboMtrl)), (BiboBase, Full("Other Skin", BiboBase)), (BiboNorm, Full("Other Skin", BiboNorm)));
        Assert.Equal(["Other Skin"], FindProviders(In(kept, candidates, resolved: resolved)).Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-7")]
    public void FindProviders_ManagedActiveMaterialIsKeptReference()
    {
        var kept = D((Managed, M(F(Managed, BiboMtrl, [BiboBase]))));
        var resolved = R((BiboMtrl, Full(Managed, BiboMtrl)), (BiboBase, Full(Clia, BiboBase)));
        Assert.Equal(Managed, FindProviders(In(kept, CliaTextures(), resolved: resolved))[Clia].Single().NeededBy);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-9")]
    public void FindProviders_ProviderAlsoReplacingShippedFiles_SparedWhole()
    {
        var (kept, resolved) = MatteCase();
        var candidates = D((Clia, M(F(Clia, BiboBase), F(Clia, BiboNorm), F(Clia, "chara/common/texture/skin_m.tex"))));
        Assert.True(FindProviders(In(kept, candidates, resolved: resolved)).ContainsKey(Clia));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-21")]
    public void FindProviders_ReleasedProvider_SparedNotHeld()
    {
        // A player-released stray is not a candidate for holding; if it is a provider it is still found and spared.
        var (kept, resolved) = MatteCase();
        Assert.True(FindProviders(In(kept, CliaTextures(), resolved: resolved)).ContainsKey(Clia));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-22")]
    public void Normalisation_BackslashMixedCase_MatchesEverywhere()
    {
        Assert.Equal("C:/Mods/Clia Skin/Chara/Bibo_Mid_Base.TEX", NormFile("|1_2_3|C:\\Mods\\Clia Skin\\Chara\\Bibo_Mid_Base.TEX"));
        Assert.Equal("chara/bibo_mid_base.tex", NormGame(" Chara\\Bibo_Mid_Base.tex "));

        var kept = D((Yab, M(new ModFile(NormGame(BiboMtrl), "C:\\MODS\\" + Yab + "\\files\\Matte.MTRL", false, ["Chara/Bibo_Mid_Base.tex"], false))));
        var candidates = D((Clia, M(new ModFile(NormGame(BiboBase), "c:/mods/" + Clia + "/files/base.tex", true, [], false))));
        var resolved = R((BiboMtrl, "C:/Mods/" + Yab + "/files/matte.mtrl"), (BiboBase, "C:\\Mods\\" + Clia + "\\Files\\BASE.tex"));
        var asked = new List<string>();
        var p = FindProviders(In(kept, candidates, resolved: resolved, exists: g => { asked.Add(g); return false; }));
        Assert.True(p.ContainsKey(Clia));
        Assert.All(asked, g => Assert.Equal(g.ToLowerInvariant().Replace('\\', '/'), g));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-25")]
    public void FindProviders_OnlyResolvedOptionFileIsActive()
    {
        // YAB+ "Pores" is the selected option; only "Matte" names the bibo_mid textures.
        var kept = D((Yab, M(F(Yab, BiboMtrl, [BiboBase, BiboNorm], def: false, tag: "matte"),
                             F(Yab, BiboMtrl, ["chara/common/texture/skin_m.tex"], def: false, tag: "pores"))));
        var resolved = R((BiboMtrl, Full(Yab, BiboMtrl, "pores")), (BiboBase, Full(Clia, BiboBase)), (BiboNorm, Full(Clia, BiboNorm)));
        Assert.Empty(FindProviders(In(kept, CliaTextures(), resolved: resolved)));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-38")]
    public void FindProviders_HeldPathResolvedByKeptOrCandidate_NotWouldSupply()
    {
        var (kept, _) = MatteCase();
        kept["Skin X"] = M(F("Skin X", BiboBase), F("Skin X", BiboNorm));
        var resolved = R((BiboMtrl, Full(Yab, BiboMtrl)), (BiboBase, Full("Skin X", BiboBase)), (BiboNorm, Full("Skin X", BiboNorm)));
        Assert.Empty(FindProviders(In(kept, held: CliaTextures(), resolved: resolved)));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-43")]
    public void FindProviders_TwoHeldStraysSamePath_BothProviders()
    {
        var (kept, _) = MatteCase();
        var held = CliaTextures();
        held["Other Skin"] = M(F("Other Skin", BiboBase));
        var resolved = R((BiboMtrl, Full(Yab, BiboMtrl)), (BiboBase, null), (BiboNorm, null));
        Assert.Equal([Clia, "Other Skin"], FindProviders(In(kept, held: held, resolved: resolved)).Keys.Order());
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-44")]
    public void ResolveQuery_Bounded()
    {
        var kept = D((Yab, M(F(Yab, BiboMtrl, def: false), F(Yab, BodyModel, def: false), F(Yab, "chara/human/c0201/obj/body/b0001/texture/x.tex", def: false),
                             F(Yab, "a/1.tex"), F(Yab, "a/2.tex"), F(Yab, "a/3.tex"), F(Yab, "a/4.tex"), F(Yab, "a/5.tex"))));
        var candidates = D((Stryx, M(F(Stryx, HairTex), F(Stryx, "chara/custom/own.tex"))));
        var sanity = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Yab };
        var q = ResolveQuery(In(kept, candidates, sanity: sanity));

        Assert.Contains(NormGame(BiboMtrl), q);
        Assert.Contains(NormGame(BodyModel), q);
        Assert.DoesNotContain("chara/human/c0201/obj/body/b0001/texture/x.tex", q);   // kept option texture
        Assert.Equal(SanitySamplePerMod, q.Count(g => g.StartsWith("a/", StringComparison.Ordinal)));
        Assert.DoesNotContain(HairTex, q);                                            // shipped stray texture
        Assert.Contains("chara/custom/own.tex", q);                                   // game-less stray file
        Assert.Equal(q.Distinct().Count(), q.Count);
    }

    // ── AC-10: evidence that can't be trusted ───────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-10")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void Blocked_KeptManifestUnreadable_NoHolds_ManagedNeverBlocks()
    {
        Assert.Equal((SweepBlock.KeptManifest, Yab), Blocked(In(D((Yab, ModDirFiles.Failed)))));
        Assert.Equal(SweepBlock.None, Blocked(In(D((Managed, ModDirFiles.Failed)))).Block);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-10")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void Blocked_ActiveKeptFileUnreadable_NoHolds_InactiveOrManagedIgnored()
    {
        var active = D((Yab, M(F(Yab, BiboMtrl, bad: true))));
        Assert.Equal(SweepBlock.KeptActiveFileUnreadable, Blocked(In(active, resolved: R((BiboMtrl, Full(Yab, BiboMtrl))))).Block);
        Assert.Equal(SweepBlock.None, Blocked(In(active, resolved: R((BiboMtrl, null)))).Block);
        var managed = D((Managed, M(F(Managed, BiboMtrl, bad: true))));
        Assert.Equal(SweepBlock.None, Blocked(In(managed, resolved: R((BiboMtrl, Full(Managed, BiboMtrl))))).Block);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-10")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-27")]
    public void ResolveLooksSane_HeldBoundDefaultsResolveNowhere_False()
    {
        var kept = D((Yab, M(F(Yab, "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl"))));
        var sanity = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Yab };
        Assert.False(ResolveLooksSane(In(kept, sanity: sanity)));   // "Enable Mods" off: nothing resolves into it
        Assert.Equal(SweepBlock.ResolveInsane, Blocked(In(kept, sanity: sanity)).Block);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-27")]
    public void ResolveLooksSane_OptionOnlyOutrankedOrProteusContentMods_True()
    {
        var sanity = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Yab };
        // Option-only bound mod (YAB+ Skin Materials Off): nothing to sample.
        Assert.True(ResolveLooksSane(In(D((Yab, M(F(Yab, BiboMtrl, def: false)))), sanity: sanity)));
        // Its default path won by another kept mod still counts as the design's collection.
        var kept = D((Yab, M(F(Yab, BiboBase))), ("Skin X", M(F("Skin X", BiboBase))));
        Assert.True(ResolveLooksSane(In(kept, resolved: R((BiboBase, Full("Skin X", BiboBase))), sanity: sanity)));
        // A Proteus content mod outranked by a stray is not in the sanity set: the sweep stays on, the stray is held.
        var proteusOnly = D(("Overlay", M(F("Overlay", BiboBase))));
        var resolved = R((BiboBase, Full("Stray", BiboBase)));
        Assert.True(ResolveLooksSane(In(proteusOnly, resolved: resolved)));
        Assert.Empty(FindProviders(In(proteusOnly, D(("Stray", M(F("Stray", BiboBase)))), resolved: resolved)));
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-10")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-42")]
    public void IsTransient_MapSettingsKeptResolveBlocks_True()
    {
        foreach (var b in Enum.GetValues<SweepBlock>().Where(b => b != SweepBlock.None)) Assert.True(IsTransient(b));
        Assert.False(IsTransient(SweepBlock.None));
    }

    // ── AC-8/9: holds the player put back ───────────────────────────────────────────────────────────────────────

    private static Dictionary<string, HoldVerdict> Classify(PenumbraBridge.TemporaryHold? hold, string? last, out Dictionary<string, string> seen)
    {
        var holds = new Dictionary<string, PenumbraBridge.TemporaryHold?>(StringComparer.OrdinalIgnoreCase) { ["Stray"] = hold };
        var lastSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (last != null) lastSource["Stray"] = last;
        return ClassifySweepHolds(holds, lastSource, DesignBindingService.TemporarySource, out seen);
    }

    private static PenumbraBridge.TemporaryHold Hold(string source, bool enabled) => new(true, false, source, enabled, 0);
    private static readonly PenumbraBridge.TemporaryHold Gone = new(false, false, "", false, 0);

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-8")]
    public void ClassifySweepHolds_GoneOrOurHoldTurnedOn_Released()
    {
        Assert.Equal(HoldVerdict.Released, Classify(Gone, "Proteus", out _)["Stray"]);
        Assert.Equal(HoldVerdict.Released, Classify(Gone, null, out _)["Stray"]);
        Assert.Equal(HoldVerdict.Released, Classify(Hold("proteus", true), "Proteus", out _)["Stray"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-12")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-14")]
    public void ClassifySweepHolds_NullQueryOrOurHoldStillOff_Keep()
    {
        Assert.Equal(HoldVerdict.Keep, Classify(null, "Proteus", out _)["Stray"]);
        Assert.Equal(HoldVerdict.Keep, Classify(Hold("Proteus", false), "Proteus", out _)["Stray"]);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-9")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-13")]
    public void ClassifySweepHolds_OtherSourcePresent_KeepAndRemember()
    {
        Assert.Equal(HoldVerdict.Keep, Classify(Hold("Glamourer", true), "Proteus", out var seen)["Stray"]);
        Assert.Equal("Glamourer", seen["Stray"]);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-9")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-32")]
    public void ClassifySweepHolds_GoneAfterOtherSource_Forget()
        => Assert.Equal(HoldVerdict.Forget, Classify(Gone, "Glamourer", out _)["Stray"]);

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-9")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-31")]
    public void DesignHoldsLost_AllGone_True_NoneOrSomeLeft_False_NullUnknown()
    {
        static PenumbraBridge.ModSettingsSnapshot S(bool temporary) => new(true, 0, new(), false, temporary);
        var effective = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>(StringComparer.OrdinalIgnoreCase)
            { ["A"] = S(false), ["B"] = S(false), ["C"] = S(true) };
        Assert.True(DesignHoldsLost(["A", "B"], effective));
        Assert.True(DesignHoldsLost(["A", "Missing"], effective));
        Assert.False(DesignHoldsLost(["A", "C"], effective));
        Assert.False(DesignHoldsLost([], effective));
        Assert.False(DesignHoldsLost(["A"], null));
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-9")]
    public void KeptDirs_EnabledBoundAndProteusPlusManaged()
    {
        static PenumbraBridge.ModSettingsSnapshot S(bool on) => new(on, 0, new(), false, false);
        var effective = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>(StringComparer.OrdinalIgnoreCase)
            { [Yab] = S(true), ["Held Off"] = S(false), ["Overlay"] = S(true), ["Overlay Off"] = S(false) };
        var kept = KeptDirs(new HashSet<string> { Yab, "Held Off" }, new HashSet<string> { "Overlay", "Overlay Off" }, Managed, effective);
        Assert.Equal(new[] { Managed, "Overlay", Yab }.Order(StringComparer.OrdinalIgnoreCase), kept.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-45")]
    public void Second_same_design_restore_before_phase_2_keeps_the_carry()
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Stray", "Pending", "Released", "Taken", "Bound" };
        var released = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Released" };
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Taken"] = "Glamourer" };
        var merged = MergeCarry(["Stray"], ["Pending", "Released", "Taken", "Bound", "Gone", Managed],
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bound" }, none, installed, Managed,
                                released, source, DesignBindingService.TemporarySource);
        Assert.Equal(["Pending", "Stray"], merged);
    }
}
