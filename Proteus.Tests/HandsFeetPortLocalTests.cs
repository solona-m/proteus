extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Women's shoes and gloves made men's from Studio and the CLI (hands-feet-port), on real mods and bodies: the one inputs
/// builder against the size set's job, the parity of the two surfaces, and Halone's Chosen — the mod the dead end was hit
/// on. Measurements go to the test output.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class HandsFeetPortLocalTests(ITestOutputHelper o) : IDisposable
{
    internal const string HalonesChosen = LocalData.Mods + ":Halone's Chosen - by Solona";
    internal const string AirForce1 = LocalData.Mods + ":Air Force 1 - by Solona";
    internal const string LavaBod = LocalData.Mods + ":LavaBod+ 2.2";

    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));
    private static byte[]? Game(string path) => GameData.Value.GetFile(path)?.Data;
    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    private readonly List<string> temp = [];

    public void Dispose()
    {
        foreach (string dir in temp)
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
    }

    private string Temp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "proteus-handsfeet-local-" + Guid.NewGuid().ToString("N")[..8]);
        temp.Add(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string StormblessedPath(string slot)
        => Path.Combine(LocalData.Path(SizeSetCrossSexLocalTests.Stormblessed), "items",
                        slot == "_sho" ? "neo-ishgardian boots of aiming" : "neo-ishgardian gloves of aiming",
                        StormblessedGamePath(slot));

    private static string StormblessedGamePath(string slot) => $"chara/equipment/e0633/model/c0201e0633{slot}.mdl";

    private static BodySizeCatalog GameWomen() => OtherRaceRefit.MadeForBelow(VanillaBodyCatalog.Key, null, null, 201, 101, Game)!;

    private static string LimbRel(string slot) => slot == "_sho" ? UvBakeInputs.TbseLegs[0].Rel : UvBakeInputs.TbseChest;

    /// <summary>The job's inputs as Studio hands them over: the drawn bytes, the game's women's body, its one limb size.</summary>
    private static CrossSexRefitJob.Inputs StudioInputs(string slot, BodySizeCatalog onto, string targetRel,
                                                        IReadOnlyCollection<string>? held = null, UVRemapService? uv = null)
    {
        var madeFor = GameWomen();
        string limb = AutoRefitDecisions.LimbOf(slot)!;
        var inputs = CrossSexRefitJob.EndInputs(File.ReadAllBytes(StormblessedPath(slot)), StormblessedGamePath(slot), 101,
                                                madeFor, madeFor.For(limb, "0201")[0], onto, [targetRel],
                                                CrossSexRefitLocalTests.Wearer(101, StormblessedGamePath(slot)), uv ?? Maps(),
                                                Substitute.For<IPluginLog>(), pushOut: true, carryPiercings: false, held,
                                                bakedModel: null, out string? why);
        Assert.True(inputs != null, why);
        return inputs!;
    }

    private void Say(CrossSexRefitJob.Size s, string what)
        => o.WriteLine($"{what}: refusal \"{s.Refusal}\", {s.Landed?.VertexCoverage:P1} matched, {s.Folds} folded");

    /// <summary>
    /// Stormblessed's boots and gloves made men's as Studio makes them, on the game's women's body onto TBSE: saved at the
    /// men's path with its EQDP entry, the foot or hand unmoved and the leg or arm moved, no foreign skin, no fold — and
    /// byte for byte what the size set's job makes of the same piece.
    /// </summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-hands-feet-port-2")]
    [Trait("Plan", "EDGE-hands-feet-port-9")]
    public void Stormblessed_end_in_studio_on_the_game_body() // [AC-hands-feet-port-2] [EDGE-hands-feet-port-9]
    {
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        foreach (string slot in new[] { "_sho", "_glv" })
        {
            var studio = CrossSexRefitJob.RunEnd(StudioInputs(slot, tbse, LimbRel(slot)));
            Assert.True(studio.Refusal.Length == 0, studio.Refusal);
            var s = Assert.Single(studio.Sizes);
            Say(s, slot);
            Assert.True(s.SaveAllowed, s.Refusal);
            Assert.Equal(0, s.Folds);
            Assert.Equal(StormblessedGamePath(slot).Replace("c0201", "c0101"), studio.Route.SavePath(StormblessedGamePath(slot)));
            Assert.NotNull(studio.Route.Eqdp(studio.Route.SavePath(StormblessedGamePath(slot))));
            Assert.Empty(RefitCore.ForeignSkin(s.Saved!.Model, [s.TargetPath]));

            // Measured inside the pass: the foot or hand held, the leg or arm moved.
            var baked = ModelPartReader.Read(studio.Baked!)!;
            var reading = KeepEnds.Read(slot, baked, studio.Baked!)!;
            float Move(int v) { var d = ((RetargetEdit)s.Plan!.Edit).DeltaAt(v); return MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z); }
            Assert.All(reading.Ends, v => Assert.True(Move(v) < 1e-6f, $"{slot}: end vertex {v} moved {Move(v) * 1000f:F2} mm"));
            Assert.Contains(Enumerable.Range(0, baked.Positions.Length / 3).Where(v => !reading.Ends.Contains(v)), v => Move(v) > 1e-4f);

            // The size set's inputs for the same piece: the whole game body, no pick.
            var batch = CrossSexRefitJob.RunEnd(new CrossSexRefitJob.Inputs(
                File.ReadAllBytes(StormblessedPath(slot)), StormblessedGamePath(slot), 101, GameWomen(), null, tbse,
                CrossSexRefitLocalTests.IsFile(tbse, LimbRel(slot)), CrossSexRefitLocalTests.Wearer(101, StormblessedGamePath(slot)),
                Maps(), Substitute.For<IPluginLog>(),
                Nude: slot == "_sho" ? CrossSexRefitJob.NudeOf(OtherRaceRefit.OnlyRace(tbse, 101).For("_dwn")) : null));
            Assert.Equal(Assert.Single(batch.Sizes).Saved!.Model, s.Saved.Model);
        }
    }

    /// <summary>The same shoe and glove from Studio's inputs and through the CLI, both made for Bibo+: the same outcome per
    /// size — the same bytes when made, the same refusal when refused. Stormblessed ships no extra skeleton or Est entry
    /// for them, so the two wearers are the same.</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-hands-feet-port-3")]
    [Trait("Plan", "AC-hands-feet-port-5")]
    [Trait("Plan", "EDGE-hands-feet-port-37")]
    public void Stormblessed_end_in_studio_equals_the_cli() // [AC-hands-feet-port-3] [AC-hands-feet-port-5] [EDGE-hands-feet-port-37]
    {
        string mod = LocalData.Path(SizeSetCrossSexLocalTests.Stormblessed);
        var redirects = PenumbraModMeta.ReadAllRedirects(mod);
        // The premise: no extra skeleton and no Est entry from the mod for these items.
        Assert.DoesNotContain(redirects, r => r.GamePath.Contains("/skeleton/", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(OtherRaceRefit.EstEntriesFor(PenumbraModMeta.TryReadGroups(mod),
                                                  PenumbraModMeta.TryReadDefaultData(mod)?.Manipulations, null));

        var bibo = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Bibo));
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        foreach (string slot in new[] { "_sho", "_glv" })
        {
            string limb = AutoRefitDecisions.LimbOf(slot)!;
            var from = bibo.For(limb, "0201")[0];
            var studio = CrossSexRefitJob.EndInputs(File.ReadAllBytes(StormblessedPath(slot)), StormblessedGamePath(slot), 101,
                                                    bibo, from, tbse, [LimbRel(slot)],
                                                    CrossSexRefitLocalTests.Wearer(101, StormblessedGamePath(slot)), Maps(),
                                                    Substitute.For<IPluginLog>(), true, false, null, null, out string? why);
            Assert.True(studio != null, why);
            var made = CrossSexRefitJob.RunEnd(studio!);

            string outDir = Temp();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            // --for-race writes its report to the console itself.
            var console = Console.Out;
            Console.SetOut(stdout);
            int exit;
            try
            {
                exit = RefitCli::Proteus.Refit.Program.Dispatch(
                    ["--garment", StormblessedPath(slot), "--race", "0201", "--for-race", "0101",
                     "--from-root", LocalData.Path(CrossSexRefitLocalTests.Bibo), "--body-root", LocalData.Path(CrossSexRefitLocalTests.Tbse),
                     "--from", from.Rel, "--to", LimbRel(slot) + "=TBSE", "--out-dir", outDir,
                     "--game", LocalData.Path(LocalData.GameData), "--uvmaps", LocalData.Path(LocalData.TbseUvMaps)], stdout, stderr);
            }
            finally { Console.SetOut(console); }
            Assert.True(exit == 0, stderr.ToString());
            var report = JsonNode.Parse(stdout.ToString())!;
            var size = Assert.Single(report["sizes"]!.AsArray())!;
            o.WriteLine($"{slot} from {from.Label}: job \"{made.Refusal}\" {made.Sizes.FirstOrDefault()?.Landed?.VertexCoverage:P1}; " +
                        $"CLI {size.ToJsonString()}");
            if (made.Refusal.Length > 0) { Assert.Equal(made.Refusal, (string?)report["refusal"]); continue; }
            var s = Assert.Single(made.Sizes);
            string? cliRefusal = (string?)size["refusal"];
            Assert.Equal(CrossSexRefitReport.Refusal(s), cliRefusal);
            if (cliRefusal == null)
                Assert.Equal(s.Saved!.Model, File.ReadAllBytes(Path.Combine(outDir, "TBSE.mdl")));
            else
                Assert.False(File.Exists(Path.Combine(outDir, "TBSE.mdl")));
        }
    }

    /// <summary>TBSE-X: the glove made onto its chest; the shoe refused on its SFW legs (no nude legs to fall back to).</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-hands-feet-port-29")]
    public void Tbse_x_glove_made_shoe_refused() // [EDGE-hands-feet-port-29]
    {
        var tbseX = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.TbseX));
        var glove = CrossSexRefitJob.RunEnd(StudioInputs("_glv", tbseX, SupportedBodies.Of("tbse-x", "_top", false)!.Sizes[0].Rel));
        Say(Assert.Single(glove.Sizes), "TBSE-X glove");
        Assert.True(glove.Sizes[0].SaveAllowed);
        var shoe = CrossSexRefitJob.ToStudio(
            CrossSexRefitJob.RunEnd(StudioInputs("_sho", tbseX, SupportedBodies.Of("tbse-x", "_dwn", false)!.Sizes[0].Rel)), 1);
        o.WriteLine($"TBSE-X shoe: {shoe.Error}");
        Assert.Null(shoe.Planned);
        Assert.Contains(Strings.Parts.RetargetRefuseCrossSexCoverageFmt[..20], shoe.Error);
    }

    /// <summary>TBSE's nude legs are offered for a shoe exactly as the size set offers them.</summary>
    [LocalDataFact(CrossSexRefitLocalTests.Tbse)]
    [Trait("Plan", "EDGE-hands-feet-port-27")]
    public void Nude_is_offered_for_shoes_like_the_size_set() // [EDGE-hands-feet-port-27]
    {
        var legs = OtherRaceRefit.OnlyRace(BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse)), 101).For("_dwn");
        var sfw = legs.First(l => RefitCore.Normal(l.Rel) == RefitCore.Normal(UvBakeInputs.TbseLegs[0].Rel));
        var nudeOf = CrossSexRefitJob.NudeOf(legs)!;
        var ours = nudeOf(sfw);
        Assert.NotNull(ours);
        Assert.Equal(RefitCore.Normal(SupportedBodies.Of("tbse", "_dwn", false)!.Sizes[0].NudeRel!), RefitCore.Normal(ours!.Rel));
        Assert.Equal(ours, SizeSetRefit.NudeFor(legs, SupportedBodies.Of("tbse", "_dwn", false)!.Sizes[0], Substitute.For<IPluginLog>())!(sfw));
        Assert.Null(nudeOf(ours));
    }

    /// <summary>The guard guards something real: the bake's model handed in as if drawn is bent a second time.</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-hands-feet-port-30")]
    public void Baked_bytes_would_bend_twice() // [EDGE-hands-feet-port-30]
    {
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var once = CrossSexRefitJob.RunEnd(StudioInputs("_glv", tbse, LimbRel("_glv")));
        var twice = CrossSexRefitJob.RunEnd(StudioInputs("_glv", tbse, LimbRel("_glv")) with { Garment = once.Baked! });
        o.WriteLine($"once: \"{once.Refusal}\", twice: \"{twice.Refusal}\"");
        Assert.NotEqual(once.Sizes[0].Saved!.Model, twice.Sizes.FirstOrDefault()?.Saved?.Model);
        Assert.Throws<InvalidOperationException>(() => CrossSexRefitJob.EndInputs(once.Baked!, StormblessedGamePath("_glv"), 101,
            GameWomen(), GameWomen().For("_top", "0201")[0], tbse, [LimbRel("_glv")],
            CrossSexRefitLocalTests.Wearer(101, StormblessedGamePath("_glv")), Maps(), Substitute.For<IPluginLog>(), true, false,
            null, once.Baked, out _));
    }

    /// <summary>A part unticked in the part list is held where it is with the hand; ticked, it moves.</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-hands-feet-port-13")]
    [Trait("Plan", "EDGE-hands-feet-port-10")]
    public void Unticked_parts_are_held() // [EDGE-hands-feet-port-13] [EDGE-hands-feet-port-10]
    {
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var free = CrossSexRefitJob.RunEnd(StudioInputs("_glv", tbse, LimbRel("_glv")));
        // The IVCS finger joints the man's skeleton lacks follow their own fingers.
        Assert.Contains(free.Moved ?? [], m => m.From == "iv_hito_c_l" && m.To == "j_hito_b_l");
        Assert.DoesNotContain(free.Moved ?? [], m => m.To == SkeletonRemap.Fallback);

        var baked = ModelPartReader.Read(free.Baked!)!;
        var reading = KeepEnds.Read("_glv", baked, free.Baked!)!;
        float Move(CrossSexRefitJob.Result r, int v)
        { var d = ((RetargetEdit)r.Sizes[0].Plan!.Edit).DeltaAt(v); return MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z); }
        // The part that moves most when free, held.
        var part = baked.Parts.Where(p => p.Island < 0)
                        .OrderByDescending(p => p.Triangles.Max(v => reading.Ends.Contains(v) ? 0f : Move(free, v))).First();
        var held = CrossSexRefitJob.RunEnd(StudioInputs("_glv", tbse, LimbRel("_glv"), [part.Label]));
        o.WriteLine($"held {part.Label}: free moved up to {part.Triangles.Max(v => Move(free, v)) * 1000f:F1} mm");
        Assert.Contains(part.Triangles, v => Move(free, v) > 1e-4f);
        Assert.All(part.Triangles, v => Assert.True(Move(held, v) < 1e-6f));
    }

    /// <summary>Without the body maps nothing is matched across the sexes, and nothing is saved.</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Tbse)]
    [Trait("Plan", "EDGE-hands-feet-port-11")]
    public void No_maps_no_save() // [EDGE-hands-feet-port-11]
    {
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var made = CrossSexRefitJob.ToStudio(CrossSexRefitJob.RunEnd(StudioInputs("_glv", tbse, LimbRel("_glv"),
                                                                                  uv: new UVRemapService(Substitute.For<IPluginLog>(), Temp()))), 1);
        o.WriteLine(made.Error);
        Assert.Null(made.Planned);
        Assert.NotEmpty(made.Error);
    }

    /// <summary>Made-for bodies without feet still give a men's shoe, refitted without the foot's shape; without legs, refused.</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-hands-feet-port-4")]
    [Trait("Plan", "EDGE-hands-feet-port-3")]
    public void Made_for_or_onto_without_feet_still_makes_it() // [EDGE-hands-feet-port-4] [EDGE-hands-feet-port-3]
    {
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var inputs = StudioInputs("_sho", tbse, LimbRel("_sho"));
        var noFeet = inputs with { MadeFor = inputs.MadeFor with { Options = inputs.MadeFor.Options.Where(b => b.Slot != "_sho").ToList() } };
        Say(Assert.Single(CrossSexRefitJob.RunEnd(noFeet).Sizes), "made for no feet");
        var ontoNoFeet = inputs with { Onto = tbse with { Options = tbse.Options.Where(b => b.Slot != "_sho").ToList() } };
        Say(Assert.Single(CrossSexRefitJob.RunEnd(ontoNoFeet).Sizes), "onto no feet");
        var noLegs = inputs with { MadeFor = inputs.MadeFor with { Options = inputs.MadeFor.Options.Where(b => b.Slot != "_dwn").ToList() } };
        Assert.Equal(string.Format(Strings.Parts.RetargetRefuseCrossSexMadeForFmt, ModelRace.Describe("0201")),
                     CrossSexRefitJob.RunEnd(noLegs).Refusal);
    }

    /// <summary>A Bibo-skinned shoe made a men's: never saved naming a skin the men's body doesn't use.</summary>
    [LocalDataFact(LocalData.GameData, AirForce1, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-hands-feet-port-19")]
    public void Foot_skinned_shoe_never_saves_foreign_skin() // [EDGE-hands-feet-port-19]
    {
        string root = LocalData.Path(AirForce1);
        string file = Directory.EnumerateFiles(root, "c0201e6116_sho.mdl", SearchOption.AllDirectories)
                               .First(f => !f.Contains("Body Retarget", StringComparison.OrdinalIgnoreCase));
        const string gamePath = "chara/equipment/e6116/model/c0201e6116_sho.mdl";
        var bibo = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Bibo));
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        byte[] bytes = File.ReadAllBytes(file);
        var garment = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", garment, bytes)!;
        var legs = bibo.For("_dwn", "0201");
        var from = BodySizeMatch.Rank(reading.LimbView, legs, bibo.PathOf, null).Best?.Option ?? legs[0];
        var inputs = CrossSexRefitJob.EndInputs(bytes, gamePath, 101, bibo, from, tbse, [LimbRel("_sho")],
                                                CrossSexRefitLocalTests.Wearer(101, gamePath), Maps(),
                                                Substitute.For<IPluginLog>(), true, false, null, null, out string? why)!;
        Assert.True(inputs != null, why);
        var r = CrossSexRefitJob.RunEnd(inputs);
        var s = r.Sizes.FirstOrDefault();
        o.WriteLine($"from {from.Label}: \"{r.Refusal}\" {(s != null ? $"\"{s.Refusal}\" {s.Landed?.VertexCoverage:P1} {s.Folds} folded" : "")}");
        if (s?.Saved != null) Assert.Empty(RefitCore.ForeignSkin(s.Saved.Model, [s.TargetPath]));
        else Assert.False(string.IsNullOrEmpty(r.Refusal + s?.Refusal));
    }

    /// <summary>Halone's Chosen, where the dead end was hit: its men's gauntlets are only the game's, its women's the
    /// author's, so the men's model points at the women's; and the women's one is offered men, with the men's model named.</summary>
    [LocalDataFact(LocalData.GameData, HalonesChosen, CrossSexRefitLocalTests.Tbse)]
    [Trait("Plan", "AC-hands-feet-port-10")]
    [Trait("Plan", "EDGE-hands-feet-port-22")]
    [Trait("Plan", "EDGE-hands-feet-port-12")]
    public void Halones_chosen_mens_gauntlets_point_at_the_womens() // [AC-hands-feet-port-10] [EDGE-hands-feet-port-22] [EDGE-hands-feet-port-12]
    {
        string root = LocalData.Path(HalonesChosen);
        const string mens = "chara/equipment/e0613/model/c0101e0613_glv.mdl", womens = "chara/equipment/e0613/model/c0201e0613_glv.mdl";
        var paths = SizeSetRefit.PiecesOf(root, Game, includeEnds: true).Select(p => p.GamePath).ToList();
        foreach (string p in paths) o.WriteLine(p);
        Assert.DoesNotContain(mens, paths);
        Assert.Contains(womens, paths);
        Assert.Equal(womens, OtherRaceRefit.WomensModelOf(mens, paths, false));

        var men = OtherRaceRefit.Offered(womens, BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse)),
                                         PenumbraModMeta.ReadAllRedirects(root), BodyRetargetWriter.ReadRecord(root), false)
                                .Single(c => c.Race == 101);
        Assert.True(men.CrossSex);
        Assert.Equal(mens, men.OwnModelPath);
    }

    /// <summary>Halone's Chosen's boot has no skin: read by where its cloth sits on the game's women's body, it gets a legs
    /// size or the hint — never a silent nothing.</summary>
    [LocalDataFact(LocalData.GameData, HalonesChosen, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-hands-feet-port-26")]
    public void Halones_chosen_boot_gets_a_size_or_the_hint() // [EDGE-hands-feet-port-26]
    {
        const string gamePath = "chara/equipment/e0613/model/c0201e0613_sho.mdl";
        byte[] bytes = File.ReadAllBytes(Path.Combine(LocalData.Path(HalonesChosen), "edengate greaves of fending", gamePath));
        var garment = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", garment, bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var game = GameWomen();
        Assert.Equal(KeepEnds.Route.NoSkin, KeepEnds.StudioLimb(reading, garment, bones, "0201", "x", game).Route);
        var read = KeepEnds.StudioLimb(reading, garment, bones, "0201", VanillaBodyCatalog.Key, game, clothWhenSkinless: true);
        o.WriteLine($"route {read.Route}, pick {read.Pick?.Label ?? "none"}");
        if (read.Pick is { } pick)
        {
            var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
            var inputs = CrossSexRefitJob.EndInputs(bytes, gamePath, 101, game, pick, tbse, [LimbRel("_sho")],
                                                    CrossSexRefitLocalTests.Wearer(101, gamePath), Maps(),
                                                    Substitute.For<IPluginLog>(), true, false, null, null, out string? why);
            Assert.True(inputs != null, why);
            var made = CrossSexRefitJob.ToStudio(CrossSexRefitJob.RunEnd(inputs!), 1);
            o.WriteLine($"made: {made.Planned?.Count ?? 0}, \"{made.Error}\" {made.Quality}");
            Assert.True(made.Planned != null || made.Error.Length > 0);
        }
        else Assert.True(RetargetInputs.CrossSexEndHint(true, read, limbChosen: false));
    }

    private string CopyOf(string localMod)
    {
        string from = LocalData.Path(localMod), dir = Temp();
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dir, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dir;
    }

    private static IReadOnlyList<BodyModIndex.Entry> Bodies(params string[] mods)
        => SizeSetBatch.ScanBodies(Path.GetDirectoryName(LocalData.Path(CrossSexRefitLocalTests.Tbse))!,
                                   mods.Select(m => Path.GetFileName(LocalData.Path(m))).ToList());

    private SizeSetRefit.RunLog SizeSetForMen(string root, IReadOnlyList<BodyModIndex.Entry> bodies, params string[] keys)
    {
        var runLog = new SizeSetRefit.RunLog();
        var gameBodies = SizeSetRefit.GameBodies(keys, Game, ends: true);
        SizeSetRefit.Run(new SizeSetRefit.Request(root, keys, false, false, bodies, Game, true,
                                                  race => gameBodies.GetValueOrDefault(race),
                                                  Across: new SizeSetRefit.AcrossInputs(_ => null)),
                         runLog, Maps(), Substitute.For<IPluginLog>(), _ => { }, CancellationToken.None);
        foreach (var n in runLog.Notes) o.WriteLine("  note: " + n);
        foreach (var w in runLog.Warnings) o.WriteLine("  check: " + w);
        return runLog;
    }

    /// <summary>
    /// Halone's Chosen's boots have no skin and sit on no body closely; their outfit's legs read as Bibo+ Type C - Small, so
    /// the men's boots are made from Bibo+, not from the game's body: TBSE made (said under Check in game), TBSE-X refused
    /// with its reason. The author's groups are unchanged; the boot goes into our men's group.
    /// </summary>
    [LocalDataFact(LocalData.GameData, HalonesChosen, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse,
                   CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-hands-feet-port-12")]
    [Trait("Plan", "EDGE-hands-feet-port-39")]
    [Trait("Plan", "EDGE-hands-feet-port-45")]
    public void Halones_chosen_boots_take_their_legs_body() // [AC-hands-feet-port-12] [EDGE-hands-feet-port-39] [EDGE-hands-feet-port-45]
    {
        string copy = CopyOf(HalonesChosen);
        static List<(string Name, JsonNode? Group)> Groups(string root)
            => PenumbraModMeta.TryReadGroups(root)!.Select(g => (g.Name, JsonNode.Parse(g.Group.GetRawText()))).ToList();
        var before = Groups(copy);
        var run = SizeSetForMen(copy, Bodies(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX),
                                "tbse@women", "tbse-x@women");
        const string boot = "chara/equipment/e0613/model/c0201e0613_sho.mdl", mensBoot = "chara/equipment/e0613/model/c0101e0613_sho.mdl";
        var bootPiece = SizeSetRefit.PiecesOf(copy, Game, includeEnds: true).Single(p => p.GamePath == boot);
        foreach (var s in run.Sizes.Where(s => s.Piece == bootPiece.Label))
            o.WriteLine($"{s.Body}: {s.Status} {s.ReasonCode} from {s.From} folded {s.Measures?.Folded} " +
                        $"worst {s.Measures?.WorstMoveMm:F1} mm — {s.Reason}");

        var tbse = Assert.Single(run.Sizes, s => s.Men && s.Body == "tbse" && s.Piece == bootPiece.Label);
        Assert.Equal(SizeSetRefit.SizeStatus.Made, tbse.Status);
        Assert.Contains("Bibo+", tbse.From);
        Assert.Contains("Type C - Small", tbse.From);
        Assert.Contains(run.Warnings, w => w == string.Format(Strings.SizeSet.EndFromOutfitLegsFmt, bootPiece.Label, "TBSE",
                                                              tbse.From, "Small"));
        Assert.DoesNotContain(run.Warnings, w => w == string.Format(Strings.SizeSet.EndFromGameBodyFmt, bootPiece.Label, "TBSE"));

        var tbseX = Assert.Single(run.Sizes, s => s.Men && s.Body == "tbse-x" && s.Piece == bootPiece.Label);
        Assert.NotEqual(SizeSetRefit.SizeStatus.Made, tbseX.Status);
        Assert.False(string.IsNullOrEmpty(tbseX.Reason));

        // Saved at the men's path, in our men's group; nothing at it in the TBSE-X option; the author's groups unchanged.
        string mensGroup = SizeSetRefit.MensGroupName(copy, SizeSetRefit.AcrossPieces(SizeSetRefit.PiecesOf(copy, Game)));
        var after = Groups(copy);
        var group = after.Single(g => g.Name == mensGroup).Group!["Options"]!.AsArray();
        Assert.Contains(group, opt => (string?)opt!["Name"] == "TBSE" && opt["Files"]?[mensBoot] != null);
        Assert.DoesNotContain(group, opt => (string?)opt!["Name"] == "TBSE-X" && opt["Files"]?[mensBoot] != null);
        foreach (var (name, json) in before.Where(g => g.Name != mensGroup))
            Assert.True(JsonNode.DeepEquals(json, after.Single(g => g.Name == name).Group), $"group {name} changed");
    }

    /// <summary>Stormblessed's ends are made as before: gloves untouched by the rule, boots still made for TBSE.</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-hands-feet-port-43")]
    public void Stormblessed_ends_no_regression() // [EDGE-hands-feet-port-43]
    {
        string copy = CopyOf(SizeSetCrossSexLocalTests.Stormblessed);
        var run = SizeSetForMen(copy, Bodies(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse), "tbse@women");
        foreach (string slot in new[] { "_sho", "_glv" })
        {
            var outcome = Assert.Single(run.Sizes, s => s.Men && s.Body == "tbse" && s.GamePath == $"chara/equipment/e0633/model/c0101e0633{slot}.mdl");
            o.WriteLine($"{slot}: {outcome.Status} from {outcome.From}");
            Assert.Equal(SizeSetRefit.SizeStatus.Made, outcome.Status);
        }
        Assert.DoesNotContain(run.Warnings, w => w.Contains("e0633") && w.Contains("Glove", StringComparison.OrdinalIgnoreCase)
                                                 && w.Contains("outfit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The size set on Halone's Chosen with "for men" bodies ticked: every women's shoe and glove has a men's outcome per
    /// men's body, or the stated exclusion of the author's own men's model — none silent — and the author's groups are
    /// untouched, the men's group last. With shoes and gloves off, they are named as left out.
    /// </summary>
    [LocalDataFact(LocalData.GameData, HalonesChosen, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse,
                   CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-hands-feet-port-9")]
    [Trait("Plan", "EDGE-hands-feet-port-17")]
    [Trait("Plan", "EDGE-hands-feet-port-25")]
    public void Halones_chosen_ends_for_men_are_never_silent() // [AC-hands-feet-port-9] [EDGE-hands-feet-port-17] [EDGE-hands-feet-port-25]
    {
        string Copy()
        {
            string dir = Temp();
            foreach (string f in Directory.EnumerateFiles(LocalData.Path(HalonesChosen), "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dir, Path.GetRelativePath(LocalData.Path(HalonesChosen), f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            return dir;
        }
        var bodies = SizeSetBatch.ScanBodies(Path.GetDirectoryName(LocalData.Path(CrossSexRefitLocalTests.Tbse))!,
                                       [Path.GetFileName(LocalData.Path(CrossSexRefitLocalTests.Bibo)),
                                        Path.GetFileName(LocalData.Path(CrossSexRefitLocalTests.Tbse)),
                                        Path.GetFileName(LocalData.Path(CrossSexRefitLocalTests.TbseX))]);
        SizeSetRefit.RunLog Run(string root, bool ends)
        {
            var runLog = new SizeSetRefit.RunLog();
            SizeSetRefit.Run(new SizeSetRefit.Request(root, ["tbse@women", "tbse-x@women"], false, false, bodies, Game, ends,
                                                      Across: new SizeSetRefit.AcrossInputs(_ => null)),
                             runLog, Maps(), Substitute.For<IPluginLog>(), _ => { }, CancellationToken.None);
            foreach (var n in runLog.Notes) o.WriteLine("  note: " + n);
            return runLog;
        }

        string copy = Copy();
        // By value: a save rewrites the manifest, so its whitespace changes and nothing else of the author's may.
        static List<(string Name, JsonNode? Group)> Groups(string root)
            => PenumbraModMeta.TryReadGroups(root)!.Select(g => (g.Name, JsonNode.Parse(g.Group.GetRawText()))).ToList();
        var groupsBefore = Groups(copy);
        var run = Run(copy, ends: true);
        foreach (var piece in SizeSetRefit.PiecesOf(copy, Game, includeEnds: true)
                                          .Where(p => p.Race == "0201" && p.Slot is "_sho" or "_glv"))
            foreach (string key in new[] { "tbse", "tbse-x" })
            {
                var outcomes = run.Sizes.Where(s => s.Men && s.Body == key && s.Piece == piece.Label).ToList();
                o.WriteLine($"{piece.Label} {key}: {string.Join("; ", outcomes.Select(s => $"{s.Status} {s.ReasonCode}"))}");
                Assert.Contains(outcomes, s => s.Status == SizeSetRefit.SizeStatus.Made
                                               || (!string.IsNullOrEmpty(s.ReasonCode) && !string.IsNullOrEmpty(s.Reason)));
            }
        Assert.DoesNotContain(run.Notes, n => n.Contains("aren't refitted across the sexes", StringComparison.Ordinal));
        // Halone's Chosen's legs: the author's own men's model stops them crossing, and that is stated — an authors-mens
        // outcome per men's body, and the note to tick the plain body.
        var womensLegs = SizeSetRefit.PiecesOf(copy, Game, includeEnds: true).Single(p => p.GamePath == "chara/equipment/e0613/model/c0201e0613_dwn.mdl");
        foreach (string key in new[] { "tbse", "tbse-x" })
            Assert.Contains(run.Sizes, s => s.ReasonCode == SizeSetRefit.Codes.AuthorsMens && s.Piece == womensLegs.Label && s.Body == key);
        Assert.Contains(string.Format(Strings.SizeSet.AuthorsMensUntickedFmt, womensLegs.Label, "TBSE"), run.Notes);
        // Only "for men" bodies ticked: an author's men's model is said to want the plain body, never to have made nothing.
        foreach (var piece in SizeSetRefit.PiecesOf(copy, Game, includeEnds: true))
            Assert.DoesNotContain(string.Format(Strings.SizeSet.AuthorsMensFmt, piece.Label), run.Notes);
        // The men's group is ours (an earlier run's, or appended last now): the new men's versions go into it. Every group
        // of the author's is unchanged, in place.
        string mensGroup = SizeSetRefit.MensGroupName(copy, SizeSetRefit.AcrossPieces(SizeSetRefit.PiecesOf(copy, Game)));
        var groupsAfter = Groups(copy);
        Assert.True(groupsAfter.Count >= groupsBefore.Count);
        for (int i = 0; i < groupsBefore.Count; i++)
        {
            Assert.Equal(groupsBefore[i].Name, groupsAfter[i].Name);
            if (groupsBefore[i].Name != mensGroup)
                Assert.True(JsonNode.DeepEquals(groupsBefore[i].Group, groupsAfter[i].Group), $"group {groupsBefore[i].Name} changed");
        }
        if (groupsBefore.All(g => g.Name != mensGroup)) Assert.Equal(mensGroup, groupsAfter[^1].Name);

        var off = Run(Copy(), ends: false);
        Assert.Contains(off.Sizes, s => s.ReasonCode == SizeSetRefit.Codes.EndsLeftOut && s.Slot == "_glv");
        Assert.Contains(off.Notes, n => n.StartsWith(Strings.SizeSet.EndsLeftOutFmt[..10], StringComparison.Ordinal));
    }
}
