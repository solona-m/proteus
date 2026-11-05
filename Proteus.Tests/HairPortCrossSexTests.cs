using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// What the cross-sex hair tests build on: a deformer where a Midlander woman's (c0201) head sits 6 cm above a
/// Midlander man's (c0101), so a port that bends nothing lands visibly off; the Lalafell on their own ancestor; caps for
/// heads and hair; the game's files for hairstyle 5 (no shared material) and 130 (the men's Midlander material).
/// </summary>
internal static class CrossSexFixtures
{
    public const int Id = 5;
    public static readonly ushort[] Races = [101, 201, 301, 401, 701, 801, 1101, 1201, 1301];
    public static readonly string Source = HairPortPlan.HairPath(201, Id);
    public static readonly string Target = HairPortPlan.HairPath(101, Id);
    public const string Bone = "j_ex_h0005_ke_b";

    public static Vector3 Off(ushort race) => race switch
    {
        201 or 401 => new Vector3(0f, 0.06f, 0f),
        801 => new Vector3(0f, 0.16f, 0f),
        1101 or 1201 => new Vector3(0f, -0.6f, 0f),
        _ => Vector3.Zero,
    };

    private static Dictionary<string, Matrix4x4> J(Matrix4x4 m) => new() { ["j_kao"] = m };
    private static Matrix4x4 Up(float y) => Matrix4x4.CreateTranslation(0f, y, 0f);

    /// <param name="womenOffset">Off: the deformer forgets the women's head — a bend that misses it.</param>
    public static PbdFile Pbd(bool womenOffset = true) => new(HairPortFixtures.BuildPbd(
    [
        (101, ushort.MaxValue, J(Matrix4x4.Identity)),
        (201, 101, J(womenOffset ? Up(0.06f) : Matrix4x4.Identity)),
        (301, 101, J(Matrix4x4.Identity)),
        (401, 201, J(Matrix4x4.Identity)),
        (701, 101, J(Matrix4x4.Identity)),
        (801, 201, J(Up(0.1f))),
        (1101, 101, J(Up(-0.6f))),
        (1201, 1101, J(Matrix4x4.Identity)),
        (1301, 101, J(Matrix4x4.Identity)),
    ]));

    public static byte[] Head(ushort race)
        => SyntheticModel.BuildSurface([], HairPortFixtures.Sphere($"/mt_c{race:D4}f0001_fac_a.mtrl", HairPortFixtures.Head + Off(race),
                                                                   0.09f, [("j_kao", 1f)]));

    /// <summary>A race's hairstyle: a 10 cm cap over its head; <paramref name="radius"/> changes the bytes, not the fit.</summary>
    public static byte[] Cap(ushort race, int id = Id, float radius = 0.1f, (string Bone, float W)[]? bones = null)
        => SyntheticModel.BuildSurface([], [HairPortFixtures.Sphere($"/mt_c{race:D4}h{id:D4}_hir_a.mtrl", HairPortFixtures.Head + Off(race),
                                                                    radius, bones ?? [("j_kao", 1f)], minY: -0.2f)]);

    public static byte[] Physics(ushort race, int id = Id, float radius = 0.1f) => Cap(race, id, radius, [(Bone, 1f)]);

    public static string Material(ushort race, int id) => HairMaterialPath.GamePath(race, id, $"/mt_c{race:D4}h{id:D4}_hir_a.mtrl")!;

    /// <summary>The game's files: each race's face, its model of each id, and the material that model reads.</summary>
    public static Dictionary<string, byte[]> GameFiles(IEnumerable<ushort>? races = null, IEnumerable<int>? ids = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var race in races ?? Races)
        {
            files[HairPortPlan.HeadPaths(race).First()] = Head(race);
            foreach (int id in ids ?? [Id])
            {
                files[HairPortPlan.HairPath(race, id)] = Cap(race, id);
                files[Material(race, id)] = [1, 2, 3, (byte)(race / 100), (byte)id];
            }
        }
        return files;
    }

    public static Func<string, byte[]?> Game(Dictionary<string, byte[]>? files = null)
    {
        files ??= GameFiles();
        return path => files.TryGetValue(path, out var b) ? b : null;
    }

    /// <summary>The game for physics hair: the table gives every race skeleton 5 for hairstyle 5, and the men's skeleton 5
    /// names the same bone as the women's — another style's chain with the same name.</summary>
    public static Dictionary<string, byte[]> PhysicsFiles(bool sourceHasBone = true, params (ushort Race, int Set, int Skeleton)[] more)
    {
        var files = GameFiles();
        files[HairSkeletonTable.GamePath] = HairPortFixtures.EstBytes([(201, Id, Id), (101, Id, Id), (401, Id, Id), (301, Id, Id), .. more]);
        files[HairPortPlan.SkeletonPath(201, Id)] = Skeleton(sourceHasBone ? Bone : "j_ex_h0005_ke_z");
        files[HairPortPlan.PhysicsPath(201, Id)] = System.Text.Encoding.ASCII.GetBytes("\0phyb\0" + Bone + "\0");
        files[HairPortPlan.SkeletonPath(101, Id)] = Skeleton(Bone, "j_ex_h0005_ke_m");
        files[HairPortPlan.SkeletonPath(401, Id)] = Skeleton(Bone);
        files[HairPortPlan.SkeletonPath(301, Id)] = Skeleton(Bone, "j_ex_h0005_ke_m");
        return files;
    }

    public static byte[] Skeleton(params string[] bones)
        => System.Text.Encoding.ASCII.GetBytes("\0\0hkaSkeleton\0" + string.Join("\0", bones) + "\0");

    /// <summary>A women's hair mod: group "Cut" with the options given, each publishing the c0201 model of
    /// <paramref name="id"/> from its own file.</summary>
    /// <param name="root">The mod's folder, when it must sit in a test's mods root.</param>
    public static HairPortFixtures.Mod WomensMod(int id = Id, bool physics = false, string[]? options = null, string? root = null)
    {
        var source = HairPortPlan.HairPath(201, id);
        options ??= ["Long"];
        var mod = new HairPortFixtures.Mod("[" + HairPortFixtures.Group("Cut", "Single",
            options.Select(o => (o, new Dictionary<string, string> { [source] = $"{o.ToLowerInvariant()}.mdl" })).ToArray()) + "]",
            root: root);
        for (int i = 0; i < options.Length; i++)
            mod.Put($"{options[i].ToLowerInvariant()}.mdl", physics ? Physics(201, id, 0.1f + i * 0.002f) : Cap(201, id, 0.1f + i * 0.002f));
        return mod;
    }

    public static void EditMeta(HairPortFixtures.Mod mod, Action<JsonNode> edit)
    {
        var meta = Path.Combine(mod.Root, "meta.json");
        var node = JsonNode.Parse(File.ReadAllText(meta))!;
        edit(node);
        File.WriteAllText(meta, node.ToJsonString());
    }

    public static JsonNode Option(JsonNode meta, string option)
        => meta["Groups"]![0]!["Options"]!.AsArray().First(o => (string?)o!["Name"] == option)!;

    /// <summary>Publish another file from an option, as an author's update does.</summary>
    public static void AddFile(HairPortFixtures.Mod mod, string option, string gamePath, string rel, byte[] bytes)
    {
        mod.Put(rel, bytes);
        EditMeta(mod, n => Option(n, option)["Files"]![gamePath] = rel);
    }

    /// <summary>The author gives a race a skeleton entry of their own in an option.</summary>
    public static void AddEst(HairPortFixtures.Mod mod, string option, string race, int id, int entry)
        => EditMeta(mod, n => Option(n, option)["Manipulations"]!.AsArray()
                                .Add(JsonSerializer.SerializeToNode(PenumbraManipulations.EstManipulation(race, "Hair", id, entry))));

    public static HairPortBuilder.Plan Build(HairPortFixtures.Mod mod, IEnumerable<ushort> targets, bool auto,
                                             Func<string, byte[]?>? game = null, string? source = null, PbdFile? pbd = null)
        => HairPortBuilder.Build(mod.Root, source ?? Source, targets, pbd ?? Pbd(), game ?? Game(), withSharers: auto);

    /// <summary>A hand port, saved.</summary>
    public static void HandPort(HairPortFixtures.Mod mod, IEnumerable<ushort> targets, Func<string, byte[]?>? game = null,
                                string? source = null)
    {
        var plan = Build(mod, targets, false, game, source);
        Assert.Empty(plan.Refused);
        var saved = HairPortWriter.Save(mod.Root, plan.Writes, auto: false);
        Assert.True(saved.Ok, saved.Message);
    }

    public static bool OutOfDate(HairPortFixtures.Mod mod, Func<string, byte[]?> game, string target)
    {
        var record = HairPortRecord.Read(mod.Root);
        var entry = record.Ports.Single(e => e.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase));
        return HairPortBuilder.IsOutOfDate(mod.Root, entry, mod.Get(entry.SourceFile), mod.Owners, record.Ports, game);
    }

    public static List<HairPortPanel.Row> Rows(HairPortFixtures.Mod mod, Func<string, byte[]?> game, string? source = null)
    {
        source ??= Source;
        int id = HairPortPlan.Parse(source)!.Value.Id;
        var record = HairPortRecord.Read(mod.Root);
        return HairPortPanel.Rows(source, mod.Owners, record, r => game(HairPortPlan.HairPath(r, id)) != null,
                                  HairPortPanel.OutOfDate(mod.Root, mod.Owners, record, game));
    }

    public static float MeanY(byte[] model)
    {
        var p = ModelPartReader.Read(model)!.Positions;
        float sum = 0;
        for (int i = 1; i < p.Length; i += 3) sum += p[i];
        return sum / (p.Length / 3);
    }

    public static string Declined => ProteusLoc.Localize("HairPort.Refused.Declined", "its port was undone, so it is left alone");
}

/// <summary>Hair ports across the sexes: by hand in the Studio, kept current by the automatic path, never started by it.</summary>
public class HairPortCrossSexTests
{
    private static readonly string Source = CrossSexFixtures.Source;
    private static readonly string Target = CrossSexFixtures.Target;
    private const int Id = CrossSexFixtures.Id;

    // ── The Studio's rows ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-1")]
    public void Other_sex_rows_follow_the_same_sex_rows_with_their_states()
    {
        using var mod = CrossSexFixtures.WomensMod();
        CrossSexFixtures.HandPort(mod, [101, 701]);
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, radius: 0.101f));   // both ports now out of date…
        var files = CrossSexFixtures.GameFiles();
        files.Remove(HairPortPlan.HairPath(1301, Id));                    // …and a man who cannot choose it
        var game = CrossSexFixtures.Game(files);
        // The c0101 port current again: remade by hand.
        Assert.True(HairPortWriter.Save(mod.Root, CrossSexFixtures.Build(mod, [101], false, game).Writes, false).Ok);

        var rows = CrossSexFixtures.Rows(mod, game);
        Assert.Equal(HairPortPlan.SameGenderRaces(201).Select(r => (r, false))
                         .Concat(HairPortPlan.OtherSexRaces(201).Select(r => (r, true))),
                     rows.Select(r => (r.Race, r.OtherSex)));
        HairPortPanel.State Of(ushort race) => rows.Single(r => r.Race == race).State;
        Assert.Equal(HairPortPanel.State.Ported, Of(101));
        Assert.Equal(HairPortPanel.State.Stale, Of(701));
        Assert.Equal(HairPortPanel.State.NoStyle, Of(1301));
        Assert.Equal(HairPortPanel.State.Open, Of(301));

        // A mod with its own men's model: that race made by the author, the other men its own version for that sex.
        using var both = CrossSexFixtures.WomensMod();
        CrossSexFixtures.AddFile(both, "Long", HairPortPlan.HairPath(301, Id), "men.mdl", CrossSexFixtures.Cap(301));
        var rows2 = CrossSexFixtures.Rows(both, CrossSexFixtures.Game());
        Assert.Equal(HairPortPanel.State.Covered, rows2.Single(r => r.Race == 301).State);
        Assert.Equal(HairPortPanel.State.OtherSexAuthored, rows2.Single(r => r.Race == 101).State);
        Assert.Equal(HairPortPanel.State.Open, rows2.Single(r => r.Race == 401).State);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-1")]
    public void Other_sex_note_names_the_hairstyle()
    {
        var note = HairPortPanel.OtherSexNote(130);
        Assert.Equal(string.Format(Strings.HairPort.OtherSexNoteFmt, 130), note);
        Assert.Equal(2, note.Split("130").Length - 1);
        Assert.Equal("Also for the other sex", Strings.HairPort.OtherSex.Split("###")[0]);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-2")]
    public void Other_sex_rows_are_not_ticked_unless_out_of_date()
    {
        HairPortPanel.Row R(ushort race, HairPortPanel.State s, bool other = true, bool own = false, bool skel = false)
            => new(race, s, other, own, skel);
        var rows = new[]
        {
            R(401, HairPortPanel.State.Open, other: false), R(601, HairPortPanel.State.Stale, other: false),
            R(101, HairPortPanel.State.Open), R(301, HairPortPanel.State.Stale), R(701, HairPortPanel.State.Stale, own: true),
            R(901, HairPortPanel.State.Stale, skel: true), R(1101, HairPortPanel.State.Ported),
            R(1301, HairPortPanel.State.NoStyle), R(1501, HairPortPanel.State.OtherSexAuthored), R(1701, HairPortPanel.State.Covered),
        };
        Assert.Equal([401, 601, 301], HairPortPanel.DefaultTicks(rows).Select(r => (int)r).ToArray());
        Assert.True(HairPortPanel.CanTick(R(101, HairPortPanel.State.Open)));
        Assert.All(rows.Where(r => r.Race is 701 or 901 or 1101 or 1301 or 1501 or 1701), r => Assert.False(HairPortPanel.CanTick(r)));
    }

    // ── Hand ports ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-3")]
    public void Hand_port_to_the_other_sex_is_bent_onto_the_mans_head_in_the_same_option()
    {
        using var mod = CrossSexFixtures.WomensMod();
        var plan = CrossSexFixtures.Build(mod, [101], auto: false);
        Assert.Empty(plan.Refused);
        var write = Assert.Single(plan.Writes);
        // Bent 6 cm down onto the man's head, as the source sits on the woman's.
        float drop = CrossSexFixtures.MeanY(mod.Get("long.mdl")) - CrossSexFixtures.MeanY(write.Model);
        Assert.InRange(drop, 0.055f, 0.065f);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);

        var owner = mod.Owner("Cut", "Long");
        var rel = HairPortPlan.FileFor(owner, Target);
        Assert.True(rel != null && HairPortPlan.IsPortFile(rel));
        var entry = HairPortRecord.Read(mod.Root).Ports.Single();
        Assert.Equal((Source, Target), (entry.SourceGamePath, entry.TargetGamePath));
        // The man's material path, pointed at what the woman's model reads (the game's, copied).
        var mtrl = HairPortPlan.FileFor(owner, CrossSexFixtures.Material(101, Id));
        Assert.NotNull(mtrl);
        Assert.Equal(CrossSexFixtures.GameFiles()[CrossSexFixtures.Material(201, Id)], mod.Get(mtrl!));
        Assert.Equal(HairPortPanel.State.Ported, CrossSexFixtures.Rows(mod, CrossSexFixtures.Game()).Single(r => r.Race == 101).State);

        Assert.True(HairPortWriter.Undo(mod.Root, e => e.SourceGamePath == Source, decline: false).Ok);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.False(mod.Has(rel!));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-3")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-12")]
    public void Same_and_other_sex_ticked_together_are_ported_in_one_save()
    {
        using var mod = CrossSexFixtures.WomensMod();
        var plan = CrossSexFixtures.Build(mod, [401, 101], auto: false);
        Assert.Equal([HairPortPlan.HairPath(101, Id), HairPortPlan.HairPath(401, Id)], plan.Writes.Select(w => w.TargetGamePath).Order());
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        var owner = mod.Owner("Cut", "Long");
        Assert.NotNull(HairPortPlan.FileFor(owner, Target));
        Assert.NotNull(HairPortPlan.FileFor(owner, HairPortPlan.HairPath(401, Id)));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-4")]
    public void A_cross_sex_port_that_misses_the_head_is_saved_with_the_fit_warning()
    {
        using var mod = CrossSexFixtures.WomensMod();
        var plan = CrossSexFixtures.Build(mod, [101], auto: false, pbd: CrossSexFixtures.Pbd(womenOffset: false));
        Assert.Empty(plan.Refused);
        Assert.Single(plan.Writes);
        Assert.Contains("c0101", plan.Warnings![101]);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
    }

    // ── The automatic path ──────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-5")]
    public void Automatic_build_refuses_a_new_cross_sex_port()
    {
        using var mod = CrossSexFixtures.WomensMod();
        var plan = CrossSexFixtures.Build(mod, [101], auto: true);
        Assert.Empty(plan.Writes);
        Assert.Equal(HairPortBuilder.OtherSexAuto, plan.Refused[101]);
        Assert.True(plan.IsVerdict(101));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-6")]
    public void A_studio_cross_sex_port_is_remade_when_the_author_changes_it()
    {
        using var mod = CrossSexFixtures.WomensMod();
        CrossSexFixtures.HandPort(mod, [101]);
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, radius: 0.101f));
        var plan = CrossSexFixtures.Build(mod, [101], auto: true);
        Assert.Empty(plan.Refused);
        var write = Assert.Single(plan.Writes);
        Assert.Equal(("Cut", "Long"), (write.Into.Group, write.Into.Option));
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-6")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-9")]
    public void An_undone_cross_sex_port_is_left_alone_but_the_studio_can_port_it()
    {
        using var mod = CrossSexFixtures.WomensMod();
        CrossSexFixtures.HandPort(mod, [101]);
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: true).Ok);
        Assert.Equal(CrossSexFixtures.Declined, CrossSexFixtures.Build(mod, [101], auto: true).Refused[101]);
        Assert.Single(CrossSexFixtures.Build(mod, [101], auto: false).Writes);
    }

    // ── The mod's own version for that sex ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-7")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-6")]
    public void A_mod_with_its_own_version_for_that_sex_is_refused()
    {
        using var mod = new HairPortFixtures.Mod("[" + HairPortFixtures.Group("Hair", "Single",
            ("Women", new() { [Source] = "w.mdl" }), ("Men", new() { [Target] = "m.mdl" })) + "]");
        mod.Put("w.mdl", CrossSexFixtures.Cap(201));
        mod.Put("m.mdl", CrossSexFixtures.Cap(101));
        var rows = CrossSexFixtures.Rows(mod, CrossSexFixtures.Game());
        Assert.Equal(HairPortPanel.State.Covered, rows.Single(r => r.Race == 101).State);
        Assert.All(rows.Where(r => r.OtherSex && r.Race != 101), r =>
        {
            Assert.Equal(HairPortPanel.State.OtherSexAuthored, r.State);
            Assert.False(HairPortPanel.CanTick(r));
        });
        Assert.Equal(Strings.HairPort.RaceOtherSexAuthored, HairPortPanel.StateText(rows.Single(r => r.Race == 301)));
        var plan = CrossSexFixtures.Build(mod, [301], auto: false);
        Assert.Empty(plan.Writes);
        Assert.Contains("c0101", plan.Refused[301]);
        Assert.Contains("port that one instead", plan.Refused[301]);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-7")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-14")]
    public void An_old_cross_sex_port_is_left_as_it_is_after_the_author_adds_their_own()
    {
        using var mod = CrossSexFixtures.WomensMod();
        CrossSexFixtures.HandPort(mod, [101]);
        CrossSexFixtures.AddFile(mod, "Long", HairPortPlan.HairPath(301, Id), "men.mdl", CrossSexFixtures.Cap(301));
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, radius: 0.101f));
        var game = CrossSexFixtures.Game();
        var portFile = HairPortRecord.Read(mod.Root).Ports.Single().File;
        var before = mod.Get(portFile);
        string manifest = mod.Manifest;

        Assert.False(CrossSexFixtures.OutOfDate(mod, game, Target));
        var row = CrossSexFixtures.Rows(mod, game).Single(r => r.Race == 101);
        Assert.Equal((HairPortPanel.State.Ported, true, false), (row.State, row.OwnVersion, row.OwnSkeleton));
        Assert.False(HairPortPanel.CanTick(row));
        Assert.Contains(Strings.HairPort.RaceOtherSexAuthored, HairPortPanel.StateText(row));

        Assert.Contains("port that one instead", CrossSexFixtures.Build(mod, [101], auto: false).Refused[101]);
        var auto = CrossSexFixtures.Build(mod, [101], auto: true);
        Assert.Contains("the port made in the Studio is left as it is", auto.Refused[101]);
        Assert.Empty(auto.Writes);
        Assert.Equal(before, mod.Get(portFile));
        Assert.Equal(manifest, mod.Manifest);
    }

    // ── Physics hair ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-9")]
    public void Cross_sex_carry_choice_has_no_target_bones()
    {
        using var mod = CrossSexFixtures.WomensMod(physics: true);
        var game = CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles());
        var model = mod.Get("long.mdl");
        HairPortSkeleton.Choice Choose(ushort to)
            => HairPortSkeleton.Choose(new HairPortSkeleton.Place(mod.Root, mod.Owners, mod.Owner("Cut", "Long"), null, [], 201, to, Id,
                                                                  game, HairSkeletonTable.Lazy(game)), model);
        // The man's skeleton 5 names the bone: across the sexes that proves nothing.
        var cross = Choose(101);
        Assert.Equal((HairPortSkeleton.Route.Carry, Id), (cross.Route, cross.From));
        Assert.Empty(cross.Bones);
        // The same files for a woman's race: what it loads has every bone, as before.
        var same = Choose(401);
        Assert.Equal((HairPortSkeleton.Route.Own, Id), (same.Route, same.Check));
        Assert.Contains(CrossSexFixtures.Bone, same.Bones);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-9")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-7")]
    public void Physics_hair_carries_the_womans_skeleton_even_when_the_mans_names_match()
    {
        using var mod = CrossSexFixtures.WomensMod(physics: true);
        var game = CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles());
        var plan = CrossSexFixtures.Build(mod, [101], auto: false, game);
        Assert.Empty(plan.Refused);
        var write = Assert.Single(plan.Writes);
        Assert.Equal((Id, Id), (write.Est, write.SkeletonFrom));
        Assert.Equal(game(HairPortPlan.SkeletonPath(201, Id)),
                     write.Materials.Single(m => m.GamePath == HairPortPlan.SkeletonPath(101, Id)).Bytes);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);

        var est = mod.Owner("Cut", "Long").Manipulations!
            .Select(m => m.GetProperty("Manipulation"))
            .Single(m => m.TryGetProperty("Slot", out var s) && s.GetString() == "Hair");
        Assert.Equal(("Male", "Midlander"), (est.GetProperty("Gender").GetString(), est.GetProperty("Race").GetString()));
        Assert.False(CrossSexFixtures.OutOfDate(mod, game, Target));
    }

    [Theory]
    [Trait("Plan", "AC-cross-sex-hair-port-9")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-23")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_skeleton_that_cannot_be_carried_refuses_the_race(bool noFreeSlot)
    {
        using var mod = CrossSexFixtures.WomensMod(physics: true);
        // Either the source's skeleton has every bone and no slot is free, or it lacks the bone.
        var files = CrossSexFixtures.PhysicsFiles(sourceHasBone: noFreeSlot);
        var inner = CrossSexFixtures.Game(files);
        // Kinematics at every slot of the man's: no skeleton may be carried there.
        Func<string, byte[]?> game = noFreeSlot
            ? path => path.StartsWith("chara/human/c0101/skeleton/hair/", StringComparison.Ordinal) && path.EndsWith(".kdb") ? [1] : inner(path)
            : inner;
        var plan = CrossSexFixtures.Build(mod, [101], auto: false, game);
        Assert.DoesNotContain(plan.Writes, w => w.TargetGamePath == Target);
        Assert.Contains("c0101", plan.Refused[101]);
        Assert.Contains(CrossSexFixtures.Bone, plan.Refused[101]);
    }

    [Theory]
    [Trait("Plan", "AC-cross-sex-hair-port-6")]
    [Trait("Plan", "AC-cross-sex-hair-port-9")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-24")]
    [InlineData(false)]
    [InlineData(true)]
    public void An_authors_later_skeleton_for_the_target_race_strips_ours_once(bool withOwnModel)
    {
        using var mod = CrossSexFixtures.WomensMod(physics: true);
        var game = CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles());
        CrossSexFixtures.HandPort(mod, [101], game);
        var entry = HairPortRecord.Read(mod.Root).Ports.Single();
        Assert.True(entry is { Est: Id, SkeletonFrom: Id });
        CrossSexFixtures.AddEst(mod, "Long", "0101", Id, 77);
        if (withOwnModel) CrossSexFixtures.AddFile(mod, "Long", HairPortPlan.HairPath(301, Id), "men.mdl", CrossSexFixtures.Cap(301));

        Assert.True(CrossSexFixtures.OutOfDate(mod, game, Target));
        var plan = CrossSexFixtures.Build(mod, [101], auto: true, game);
        Assert.Contains(withOwnModel ? "the port made in the Studio is left as it is" : "a skeleton of its own", plan.Refused[101]);
        var strip = Assert.Single(plan.Writes);
        Assert.True(strip.Stripped);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);

        var owner = mod.Owner("Cut", "Long");
        Assert.Equal([77], HairPortPlan.HairEsts(owner, 101, Id).ToArray());
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.SkeletonPath(101, Id)));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.PhysicsPath(101, Id)));
        Assert.Equal(entry.File, HairPortPlan.FileFor(owner, Target));
        Assert.NotNull(HairPortPlan.FileFor(owner, CrossSexFixtures.Material(101, Id)));
        if (withOwnModel) Assert.Equal("men.mdl", HairPortPlan.FileFor(owner, HairPortPlan.HairPath(301, Id)));
        Assert.True(HairPortRecord.Read(mod.Root).Ports.Single() is { Est: 0, SkeletonFrom: 0 });

        mod.Put("long.mdl", CrossSexFixtures.Physics(201, radius: 0.101f));
        Assert.False(CrossSexFixtures.OutOfDate(mod, game, Target));
        Assert.Empty(CrossSexFixtures.Build(mod, [101], auto: true, game).Writes);
        var row = CrossSexFixtures.Rows(mod, game).Single(r => r.Race == 101);
        Assert.True(row.OwnSkeleton);
        Assert.Equal(withOwnModel, row.OwnVersion);
        Assert.False(HairPortPanel.CanTick(row));
        Assert.Contains(Strings.HairPort.RaceOtherSexSkeleton, HairPortPanel.StateText(row));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-9")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-25")]
    public void A_harmful_carry_is_stripped_once_even_with_the_authors_own_version()
    {
        using var mod = CrossSexFixtures.WomensMod(physics: true);
        CrossSexFixtures.HandPort(mod, [101], CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles()));
        CrossSexFixtures.AddFile(mod, "Long", HairPortPlan.HairPath(301, Id), "men.mdl", CrossSexFixtures.Cap(301));
        // The game now gives slot 5 of the man's to his hairstyle 9 as well.
        var game = CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles(true, (101, 9, Id)));

        Assert.True(CrossSexFixtures.OutOfDate(mod, game, Target));
        var plan = CrossSexFixtures.Build(mod, [101], auto: true, game);
        Assert.Contains("the port made in the Studio is left as it is", plan.Refused[101]);
        Assert.True(Assert.Single(plan.Writes).Stripped);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);
        Assert.False(CrossSexFixtures.OutOfDate(mod, game, Target));
        Assert.Empty(CrossSexFixtures.Build(mod, [101], auto: true, game).Writes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-26")]
    public void A_stripped_sharer_counts_as_current_in_a_sharers_remake()
    {
        const int id = 130;
        string source = HairPortPlan.HairPath(201, id);
        var files = CrossSexFixtures.GameFiles([101, 201, 1101], [id]);
        files[HairSkeletonTable.GamePath] = HairPortFixtures.EstBytes([(201, id, id), (101, id, id), (1101, id, id)]);
        files[HairPortPlan.SkeletonPath(201, id)] = CrossSexFixtures.Skeleton("j_ex_h0130_ke_b");
        var game = CrossSexFixtures.Game(files);
        using var mod = CrossSexFixtures.WomensMod(id);
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, id, bones: [("j_ex_h0130_ke_b", 1f)]));
        CrossSexFixtures.HandPort(mod, [101, 1101], game, source);
        CrossSexFixtures.AddEst(mod, "Long", "0101", id, 77);
        var strip = CrossSexFixtures.Build(mod, [101], auto: true, game, source);
        Assert.True(HairPortWriter.Save(mod.Root, strip.Writes, auto: true).Ok);
        Assert.Contains(HairPortRecord.Read(mod.Root).Ports, e => e.TargetGamePath == HairPortPlan.HairPath(101, id) && e.SkeletonFrom == 0);
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, id, 0.101f, [("j_ex_h0130_ke_b", 1f)]));

        var plan = CrossSexFixtures.Build(mod, [1101], auto: true, game, source);
        Assert.Empty(plan.Refused);
        Assert.Contains((ushort)101, plan.Current);
        var write = Assert.Single(plan.Writes, w => !w.Stripped);
        Assert.Equal(HairPortPlan.HairPath(1101, id), write.TargetGamePath);
        Assert.DoesNotContain(plan.Writes, w => w.TargetGamePath == HairPortPlan.HairPath(101, id));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-27")]
    public void Strips_judges_crossing_by_the_ports_own_source()
    {
        using var mod = CrossSexFixtures.WomensMod(physics: true);
        string highlander = HairPortPlan.HairPath(301, Id);
        CrossSexFixtures.HandPort(mod, [301], CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles()));
        Assert.True(HairPortRecord.Read(mod.Root).Ports.Single() is { SkeletonFrom: Id });
        // The author's own man's model, on a bone a Highlander lacks, and a skeleton of theirs for the Highlander.
        CrossSexFixtures.AddFile(mod, "Long", Target, "man.mdl", CrossSexFixtures.Cap(101, bones: [("j_ex_h0005_ke_q", 1f)]));
        CrossSexFixtures.AddEst(mod, "Long", "0301", Id, 77);
        var game = CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles());

        var plan = CrossSexFixtures.Build(mod, [301], auto: false, game, Target);
        Assert.True(plan.Refused.ContainsKey(301));
        var strip = Assert.Single(plan.Writes);
        Assert.True(strip.Stripped);
        Assert.Equal(Source, strip.SourceGamePath);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        var owner = mod.Owner("Cut", "Long");
        Assert.Equal([77], HairPortPlan.HairEsts(owner, 301, Id).ToArray());
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.SkeletonPath(301, Id)));
        Assert.NotNull(HairPortPlan.FileFor(owner, highlander));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-22")]
    public void An_authors_skeleton_for_the_target_race_refuses_it()
    {
        using var mod = CrossSexFixtures.WomensMod(physics: true);
        CrossSexFixtures.AddEst(mod, "Long", "0101", Id, 77);
        var plan = CrossSexFixtures.Build(mod, [101], auto: false, CrossSexFixtures.Game(CrossSexFixtures.PhysicsFiles()));
        Assert.DoesNotContain(plan.Writes, w => w.TargetGamePath == Target);
        Assert.Contains("a skeleton of its own", plan.Refused[101]);
    }

    // ── Other edge cases ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-1")]
    public void Miqote_womans_ears_come_off_for_a_midlander_man_and_stay_for_a_miqote_man()
    {
        var src = HairPortFixtures.EaredHairModel(801);
        var pbd = CrossSexFixtures.Pbd();
        var toMan = HairPortBaker.Prepare(src, 801, 101, HairPortFixtures.Id, pbd, new HashSet<string>(),
                                          CrossSexFixtures.Head(801), CrossSexFixtures.Head(101), out var why1);
        var toMiqo = HairPortBaker.Prepare(src, 801, 701, HairPortFixtures.Id, pbd, new HashSet<string>(),
                                           CrossSexFixtures.Head(801), CrossSexFixtures.Head(701), out var why2);
        Assert.True(toMan != null, why1);
        Assert.True(toMiqo != null, why2);
        Assert.True(toMan!.FurHidden > 0);
        Assert.Equal(0, toMiqo!.FurHidden);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-2")]
    public void Lalafell_crosses_through_its_own_ancestor()
    {
        Assert.Equal(1101, RacialModelBake.CommonAncestor(1201, 1101));
        Assert.Equal(101, RacialModelBake.CommonAncestor(1101, 201));
        using var lala = new HairPortFixtures.Mod(defaults: new Dictionary<string, string> { [HairPortPlan.HairPath(1201, Id)] = "f.mdl" });
        lala.Put("f.mdl", CrossSexFixtures.Cap(1201));
        Assert.Single(CrossSexFixtures.Build(lala, [1101], false, source: HairPortPlan.HairPath(1201, Id)).Writes);
        using var lalaMan = new HairPortFixtures.Mod(defaults: new Dictionary<string, string> { [HairPortPlan.HairPath(1101, Id)] = "m.mdl" });
        lalaMan.Put("m.mdl", CrossSexFixtures.Cap(1101));
        var plan = CrossSexFixtures.Build(lalaMan, [201], false, source: HairPortPlan.HairPath(1101, Id));
        var write = Assert.Single(plan.Writes);
        // Up from a Lalafell man's head to a Midlander woman's: 66 cm.
        Assert.InRange(CrossSexFixtures.MeanY(write.Model) - CrossSexFixtures.MeanY(CrossSexFixtures.Cap(1101)), 0.655f, 0.665f);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-4")]
    public void A_man_who_cannot_choose_the_hairstyle_is_refused_and_not_ticked()
    {
        using var mod = CrossSexFixtures.WomensMod();
        var files = CrossSexFixtures.GameFiles();
        files.Remove(Target);
        var game = CrossSexFixtures.Game(files);
        var row = CrossSexFixtures.Rows(mod, game).Single(r => r.Race == 101);
        Assert.Equal(HairPortPanel.State.NoStyle, row.State);
        Assert.False(HairPortPanel.CanTick(row));
        Assert.Contains("c0101", CrossSexFixtures.Build(mod, [101], false, game).Refused[101]);
    }

    /// <summary>Hairstyle 130: every man but a Hrothgar reads the Midlander man's material for it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-5")]
    public void Shared_mens_material_needs_every_man_ticked()
    {
        const int id = 130;
        string source = HairPortPlan.HairPath(201, id);
        var files = CrossSexFixtures.GameFiles([101, 201, 1101], [id]);
        var game = CrossSexFixtures.Game(files);
        using var mod = CrossSexFixtures.WomensMod(id);
        var alone = CrossSexFixtures.Build(mod, [101], false, game, source);
        Assert.Empty(alone.Writes);
        Assert.Contains("c1101", alone.Refused[101]);

        var both = CrossSexFixtures.Build(mod, [101, 1101], false, game, source);
        Assert.Empty(both.Refused);
        Assert.Equal(2, both.Writes.Count);
        var mens = CrossSexFixtures.Material(101, id);
        Assert.Equal(CrossSexFixtures.Material(1101, id), mens);
        Assert.All(both.Writes, w => Assert.Equal(files[CrossSexFixtures.Material(201, id)],
                                                  w.Materials.Single(m => m.GamePath == mens).Bytes));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-8")]
    public void An_automatic_remake_never_adds_a_new_cross_sex_sharer()
    {
        const int id = 130;
        string source = HairPortPlan.HairPath(201, id);
        using var mod = CrossSexFixtures.WomensMod(id);
        CrossSexFixtures.HandPort(mod, [101, 1101], CrossSexFixtures.Game(CrossSexFixtures.GameFiles([101, 201, 1101], [id])), source);
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, id, 0.101f));
        // A Roegadyn man can now choose it too: he would read the same material.
        var game = CrossSexFixtures.Game(CrossSexFixtures.GameFiles([101, 201, 1101, 1301], [id]));

        var plan = CrossSexFixtures.Build(mod, [101], auto: true, game, source);
        Assert.Equal(HairPortBuilder.OtherSexAuto, plan.Refused[1301]);
        Assert.Contains("c1301", plan.Refused[101]);
        Assert.Contains("c1301", plan.Refused[1101]);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-10")]
    public void An_opened_cross_sex_port_stands_for_its_source()
    {
        using var mod = CrossSexFixtures.WomensMod();
        CrossSexFixtures.HandPort(mod, [101]);
        var record = HairPortRecord.Read(mod.Root);
        Assert.Equal(Source, HairPortPlan.AuthorSource(mod.Owners, record, Target, record.Ports.Single().File));
        var row = CrossSexFixtures.Rows(mod, CrossSexFixtures.Game()).Single(r => r.Race == 101);
        Assert.Equal((HairPortPanel.State.Ported, true), (row.State, row.OtherSex));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-11")]
    public void Every_option_serving_the_hairstyle_gets_its_own_cross_sex_port()
    {
        using var mod = CrossSexFixtures.WomensMod(options: ["Long", "Short"]);
        var plan = CrossSexFixtures.Build(mod, [101], auto: false);
        Assert.Equal(["Long", "Short"], plan.Writes.Select(w => w.Into.Option).Order());
        Assert.All(plan.Writes, w => Assert.Equal(Target, w.TargetGamePath));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-15")]
    public void Version_is_unchanged() => Assert.Equal(1, HairPortBaker.Version);

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-16")]
    public void A_cross_sex_port_is_plain_redirects_in_the_authors_option()
    {
        using var mod = CrossSexFixtures.WomensMod();
        int groups = JsonNode.Parse(mod.Manifest)!["Groups"]!.AsArray().Count;
        CrossSexFixtures.HandPort(mod, [101]);
        var meta = JsonNode.Parse(mod.Manifest)!;
        Assert.Equal(4, (int)meta["FileVersion"]!);
        Assert.Equal(groups, meta["Groups"]!.AsArray().Count);
        Assert.Equal("Cut", (string?)meta["Groups"]![0]!["Name"]);
        Assert.Empty(meta["DefaultData"]!["Files"]!.AsObject());
        var option = CrossSexFixtures.Option(meta, "Long");
        Assert.True(HairPortPlan.IsPortFile((string)option["Files"]![Target]!));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-17")]
    public void An_automatic_remake_adds_no_redirect_to_a_new_option()
    {
        using var mod = CrossSexFixtures.WomensMod();
        CrossSexFixtures.HandPort(mod, [101]);
        mod.Put("short.mdl", CrossSexFixtures.Cap(201, radius: 0.102f));
        CrossSexFixtures.EditMeta(mod, n => n["Groups"]![0]!["Options"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        {
            Name = "Short", Description = "", Files = new Dictionary<string, string> { [Source] = "short.mdl" },
            FileSwaps = new Dictionary<string, string>(), Manipulations = Array.Empty<object>(),
        })));
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, radius: 0.101f));

        var plan = CrossSexFixtures.Build(mod, [101], auto: true);
        Assert.Equal(["Long"], plan.Writes.Select(w => w.Into.Option));
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target));

        // An option renamed since: no place holds the port any more, and none is made.
        CrossSexFixtures.EditMeta(mod, n => CrossSexFixtures.Option(n, "Long")["Name"] = "Longer");
        var renamed = CrossSexFixtures.Build(mod, [101], auto: true);
        Assert.Equal(HairPortBuilder.OtherSexAuto, renamed.Refused[101]);
        Assert.Empty(renamed.Writes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-18")]
    public void A_recorded_port_with_other_case_and_slashes_still_counts()
    {
        using var mod = CrossSexFixtures.WomensMod();
        CrossSexFixtures.HandPort(mod, [101]);
        var record = HairPortRecord.Read(mod.Root);
        foreach (var e in record.Ports)
        {
            e.TargetGamePath = e.TargetGamePath.ToUpperInvariant().Replace('/', '\\');
            e.SourceGamePath = e.SourceGamePath.ToUpperInvariant().Replace('/', '\\');
        }
        HairPortRecord.Write(mod.Root, record);
        Assert.Single(HairPortRecord.Read(mod.Root).Ports);
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, radius: 0.101f));

        var plan = CrossSexFixtures.Build(mod, [101], auto: true);
        Assert.Empty(plan.Refused);
        Assert.Single(plan.Writes);
    }
}
