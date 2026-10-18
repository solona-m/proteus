using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Proteus.Services;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Baking a garment the game only ships for one race into a model of another, the way the game bends it at draw time.
/// </summary>
public class RacialModelBakeTests(ITestOutputHelper o)
{
    private static bool None(ushort _) => false;

    [Theory]
    [InlineData(101, 201, 201)]    // Midlander woman in men's boots: her own race
    [InlineData(101, 401, 201)]    // Miqo'te woman: the shared female shape, which the game bends onto her
    [InlineData(101, 1801, 201)]   // Viera woman: the same
    [InlineData(101, 1201, 1201)]  // Lalafell woman falls through Lalafell men, so her own race is the last woman's
    [InlineData(101, 101, 0)]      // the garment is already hers
    [InlineData(101, 901, 0)]      // a man in men's boots: refitted as it is
    [InlineData(201, 401, 0)]      // a woman's garment on a woman: the bodies are made for it
    [InlineData(301, 201, 0)]      // not on her fall-through chain: the game would never draw it on her
    [InlineData(101, 0, 0)]        // no character to read
    public void The_target_race_follows_the_fall_through(int garment, int wearer, int expected)
        => Assert.Equal((ushort)expected, RacialModelBake.Target((ushort)garment, (ushort)wearer, None));

    [Fact]
    public void A_body_mod_with_the_wearer_s_own_race_is_preferred()
        => Assert.Equal((ushort)401, RacialModelBake.Target(101, 401, code => code is 401 or 201));

    [Fact]
    public void A_man_is_baked_only_when_his_body_mod_is_for_his_own_race()
    {
        Assert.Equal((ushort)0, RacialModelBake.Target(101, 901, code => code == 101));
        Assert.Equal((ushort)901, RacialModelBake.Target(101, 901, code => code == 901));
    }

    [Theory]
    [InlineData("chara/equipment/e6023/model/c0101e6023_sho.mdl", "chara/equipment/e6023/model/c0201e6023_sho.mdl")]
    [InlineData("chara/equipment/e0100/model/c0101e0100_top.mdl", "chara/equipment/e0100/model/c0201e0100_top.mdl")]
    public void The_save_path_names_the_new_race(string drawn, string saved)
        => Assert.Equal(saved, RacialModelBake.WithRace(drawn, 201));

    [Fact]
    public void The_switch_is_an_EQDP_entry_for_the_set_and_slot()
    {
        var sw = RacialModelBake.Switch("chara/equipment/e6023/model/c0201e6023_sho.mdl", 201);
        Assert.NotNull(sw);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(sw)).RootElement;
        Assert.Equal("Eqdp", json.GetProperty("Type").GetString());
        var m = json.GetProperty("Manipulation");
        Assert.Equal("Feet", m.GetProperty("Slot").GetString());
        Assert.Equal(6023, m.GetProperty("SetId").GetInt32());
        Assert.Equal("Female", m.GetProperty("Gender").GetString());
        Assert.Equal("Midlander", m.GetProperty("Race").GetString());
        Assert.Equal(3, m.GetProperty("ShiftedEntry").GetInt32());
    }

    // ── against the game's own files ────────────────────────────────────────

    private static readonly string[] Candidates =
    [
        "chara/equipment/e0100/model/c0101e0100_sho.mdl",
        "chara/equipment/e0005/model/c0101e0005_sho.mdl",
        "chara/equipment/e0036/model/c0101e0036_sho.mdl",
        "chara/equipment/e0001/model/c0101e0001_sho.mdl",
    ];

    private (byte[] Model, PbdFile Pbd, string Path) Load()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var pbd = new PbdFile(data.GetFile("chara/xls/boneDeformer/human.pbd")!.Data);
        foreach (string path in Candidates)
            if (data.GetFile(path) is { } file)
            {
                o.WriteLine($"garment: {path}");
                return (file.Data, pbd, path);
            }
        throw new InvalidOperationException("none of the candidate boots is in the game data");
    }

    /// <summary>
    /// The bake puts every vertex where <see cref="LiveMeshPoser"/> — the same bend the live overlay lines up with the
    /// character — puts it on a woman in the rest pose.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    public void The_bake_matches_the_bend_the_game_draws()
    {
        var (model, pbd, path) = Load();
        var baked = RacialModelBake.Bake(model, 101, 201, pbd, _ => null, out string why);
        Assert.True(baked != null, why);

        var before = ModelPartReader.Read(model)!;
        var after = ModelPartReader.Read(baked!)!;
        Assert.Equal(before.Positions.Length, after.Positions.Length);
        Assert.Equal(before.Parts.Count, after.Parts.Count);

        var mesh = ModelSkinReader.Read(model, null, path)!;
        var bones = mesh.BoneNames;
        var pose = new LivePose();
        pose.Set(bones, bones.Select(_ => -1).ToArray(), bones.Select(_ => Matrix4x4.Identity).ToArray(),
                 bones.Select(_ => Matrix4x4.Identity).ToArray(), Matrix4x4.Identity, 201);
        var world = new Vector3[mesh.VertexCount];
        new LiveMeshPoser().Pose(mesh, pose, pbd, world, 101);

        // Every vertex, shape keys' spares included: the game skins a spare with its own weights (the boots carry
        // shp_leg, whose 194 spares are named by 552 index slots).
        float worst = 0f, moved = 0f;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var p = new Vector3(after.Positions[v * 3], after.Positions[v * 3 + 1], after.Positions[v * 3 + 2]);
            var q = new Vector3(before.Positions[v * 3], before.Positions[v * 3 + 1], before.Positions[v * 3 + 2]);
            worst = MathF.Max(worst, Vector3.Distance(p, world[v]));
            moved = MathF.Max(moved, Vector3.Distance(p, q));
        }
        o.WriteLine($"{mesh.VertexCount} vertices; furthest moved {moved * 1000f:F2} mm; " +
                    $"worst against the poser {worst * 1000f:F3} mm");
        Assert.True(moved > 1e-3f, "a man's boot bent onto a woman should move");
        Assert.True(worst < 1e-3f, $"the bake is {worst * 1000f:F3} mm off the poser");
    }

    [LocalDataFact(LocalData.GameData)]
    public void Unbaking_a_bake_gives_the_original_back()
    {
        var (model, pbd, _) = Load();
        var baked = RacialModelBake.Bake(model, 101, 201, pbd, _ => null, out string why);
        Assert.True(baked != null, why);
        var back = RacialModelBake.Unbake(baked!, 101, 201, pbd, _ => null, out why);
        Assert.True(back != null, why);

        var a = ModelPartReader.Read(model)!.Positions;
        var b = ModelPartReader.Read(back!)!.Positions;
        float worst = 0f;
        for (int i = 0; i < a.Length; i++) worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
        o.WriteLine($"round trip worst {worst * 1000f:F4} mm");
        Assert.True(worst < 5e-4f, $"the round trip is {worst * 1000f:F4} mm off");
    }

    [LocalDataTheory(LocalData.GameData)]
    [InlineData("/mt_c0201b0001_bibo.mtrl")]   // longer than the name it replaces
    [InlineData("/mt_x.mtrl")]                  // shorter
    public void Renaming_a_material_moves_every_name_after_it(string to)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var model = data.GetFile("chara/equipment/e0005/model/c0101e0005_top.mdl")!.Data;
        var before = SecondSkinWriter.Parse(model);
        string from = before.MatNames[0];

        var renamed = ModelAttributeWriter.RenameMaterial(model, from, to);
        var after = SecondSkinWriter.Parse(renamed);
        o.WriteLine($"{from} -> {to}: {model.Length} -> {renamed.Length} bytes; mats [{string.Join(", ", after.MatNames)}]");

        Assert.Equal([to, .. before.MatNames.Skip(1)], after.MatNames);
        Assert.Equal(before.BoneNames, after.BoneNames);
        Assert.Equal(before.AttrNames, after.AttrNames);
        Assert.Equal(before.Shapes.Keys.OrderBy(k => k), after.Shapes.Keys.OrderBy(k => k));
        Assert.Equal(0, renamed.Length % 4 - model.Length % 4);
        Assert.Equal(ModelPartReader.Read(model)!.Positions, ModelPartReader.Read(renamed)!.Positions);

        // Read as Penumbra and TexTools read it: the block walked as a list of the declared count, which has to give
        // the same names in the same groups (see ModelStringBlockOrderTests).
        var walked = Walk(renamed, after);
        Assert.Equal(Walk(model, before).Select(n => n == from ? to : n), walked);
        Assert.Equal(after.AttrNames, walked.Take(after.AttrNames.Length));
        Assert.Equal(after.BoneNames, walked.Skip(after.AttrNames.Length).Take(after.BoneNames.Length));
        Assert.Contains(to, walked);
    }

    private static List<string> Walk(byte[] mdl, SecondSkinWriter.Source p)
    {
        uint count = BitConverter.ToUInt32(mdl, p.DeclEnd);
        var names = new List<string>();
        for (int i = 0, pos = 0; i < count && pos < p.StrSize; i++)
        {
            int end = pos;
            while (end < p.StrSize && mdl[p.StrBlock + end] != 0) end++;
            names.Add(System.Text.Encoding.ASCII.GetString(mdl, p.StrBlock + pos, end - pos));
            pos = end + 1;
        }
        return names;
    }

    [LocalDataFact(LocalData.GameData)]
    public void The_bake_keeps_the_model_s_layout()
    {
        var (model, pbd, _) = Load();
        var baked = RacialModelBake.Bake(model, 101, 201, pbd, _ => null, out string why)!;
        Assert.True(baked != null, why);

        var s = SecondSkinWriter.Parse(model);
        var t = SecondSkinWriter.Parse(baked);
        Assert.Equal(model.Length, baked.Length);
        Assert.Equal(s.MeshCount, t.MeshCount);
        Assert.Equal(s.Shapes.Count, t.Shapes.Count);
        Assert.Equal(s.BoneNames, t.BoneNames);
    }

    /// <summary>A port between races on one line is exactly the bake or unbake along it.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-race-port-25")]
    public void Port_down_or_up_the_line_equals_bake_or_unbake()
    {
        var (model, pbd, _) = Load();
        var up = RacialModelBake.Port(model, 101, 201, pbd, _ => null, out string why);
        Assert.True(up != null, why);
        Assert.Equal(ModelPartReader.Read(RacialModelBake.Bake(model, 101, 201, pbd, _ => null, out _)!)!.Positions,
                     ModelPartReader.Read(up!)!.Positions);

        var down = RacialModelBake.Port(up!, 201, 101, pbd, _ => null, out why);
        Assert.True(down != null, why);
        var a = ModelPartReader.Read(RacialModelBake.Unbake(up!, 101, 201, pbd, _ => null, out _)!)!.Positions;
        var b = ModelPartReader.Read(down!)!.Positions;
        float worst = 0f;
        for (int i = 0; i < a.Length; i++) worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
        Assert.True(worst < 1e-5f, $"{worst * 1000f:F4} mm apart");
    }

    /// <summary>Across lines, a port is an unbake to the race both fall to and a bake from there, in one pass.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-race-port-25")]
    public void Port_through_common_ancestor_equals_unbake_then_bake()
    {
        var (model, pbd, _) = Load();
        // A Lalafell woman's (c1201 → c1101 → c0101) boots onto a Midlander woman (c0201 → c0101).
        var lala = RacialModelBake.Bake(model, 101, 1201, pbd, _ => null, out string why)!;
        Assert.True(lala != null, why);
        var port = RacialModelBake.Port(lala, 1201, 201, pbd, _ => null, out why);
        Assert.True(port != null, why);
        var twoStep = RacialModelBake.Bake(RacialModelBake.Unbake(lala, 101, 1201, pbd, _ => null, out _)!, 101, 201, pbd,
                                           _ => null, out _)!;
        var a = ModelPartReader.Read(port!)!.Positions;
        var b = ModelPartReader.Read(twoStep)!.Positions;
        float worst = 0f;
        for (int i = 0; i < a.Length; i++) worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
        o.WriteLine($"one pass against two: worst {worst * 1000f:F4} mm");
        Assert.True(worst < 1e-5f, $"{worst * 1000f:F4} mm apart");
    }
}
