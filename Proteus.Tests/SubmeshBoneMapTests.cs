using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A submesh's BONE MAP is a window into the model's bone-name list: each entry is a bone INDEX, saying which bones
/// the submesh uses. It is not what the game skins with — the game reads the mesh's bone TABLE, and a vertex's blend
/// bytes are slots in that table.
/// <para/>
/// Which is why getting the map wrong is so quiet. A refit that rewrote a garment's weights published the map as the
/// slots 0..n-1 instead of the table's entries, and the result animated perfectly in game while every tool that reads
/// a model — TexTools, and so 3ds Max — named the wrong bone for every vertex: a sleeve weighted to the elbow came up
/// labelled with whatever bone happened to sit at that place in the file's name list, a belly jiggle bone in the case
/// that found this. Nobody can check or hand-fix a refit whose bone names are fiction.
/// <para/>
/// The two agree only when a mesh's table happens to be the identity, which is why these fixtures give the host a
/// table in a different order from its name list — as every real garment has.
/// </summary>
public class SubmeshBoneMapTests
{
    private const string Material = "/mt_c0201e0485_a.mtrl";

    /// <summary>Bones in NAME-list order: "n_root" first (the fixture always interns it), then first use.</summary>
    private static readonly string[] Names = ["n_root", "j_hij_l", "j_sebo_c", "iv_fukubu_phys"];

    /// <summary>The same bones in TABLE order — deliberately not the name order.</summary>
    private static readonly string[] TableOrder = ["iv_fukubu_phys", "n_root", "j_sebo_c", "j_hij_l"];

    private static byte[] Garment() => SyntheticModel.Build(
        ["atr_top"],
        [new SyntheticModel.Mesh(Material,
            new SyntheticModel.Sub(0, Weights: [("j_hij_l", 0.4f), ("j_sebo_c", 0.3f), ("iv_fukubu_phys", 0.3f)]))],
        SyntheticModel.V6,
        boneTableOrder: TableOrder);

    /// <summary>Rebuild the garment, giving every vertex of mesh 0 the influences named.</summary>
    private static SecondSkinWriter.Source Refit(params (string Bone, float W)[] to)
    {
        var plan = new (string Bone, float W)[]?[] { to, to, to };
        var rebuilt = SecondSkinWriter.Build([], [], Garment(), out _,
                                             hostReskin: m => m == 0 ? plan : null);
        return SecondSkinWriter.Parse(rebuilt);
    }

    /// <summary>Bone names a submesh's map window claims, in order.</summary>
    private static List<string> MappedNames(SecondSkinWriter.Source p, int start, int count)
        => Enumerable.Range(start, count)
                     .Select(k => p.SubmeshBoneMap[k] < p.BoneNames.Length
                                      ? p.BoneNames[p.SubmeshBoneMap[k]] : $"?{p.SubmeshBoneMap[k]}")
                     .ToList();

    /// <summary>
    /// The case from the report. The map has to name the bones the table names — the same set, and each map entry
    /// resolving to the bone at that same slot of the table.
    /// </summary>
    [Fact]
    public void A_reskinned_meshs_bone_map_names_the_bones_its_table_names()
    {
        var p = Refit(("j_hij_l", 1f));

        var table = Assert.Single(p.BoneTables);
        var tableNames = table.Select(b => p.BoneNames[b]).ToList();

        // Submesh 0 of the one mesh: the window the writer published for it.
        int start = Window(p, 12), count = Window(p, 14);
        Assert.Equal(table.Length, count);
        Assert.Equal(tableNames, MappedNames(p, start, count));
    }

    /// <summary>
    /// And specifically not the slots. Written as 0..n-1 the map still resolves to real bones — every entry is inside
    /// the name list, so nothing looks wrong to a validator — it just names the wrong ones. This pins the actual
    /// names, so the assertion above cannot be satisfied by a coincidence of ordering.
    /// </summary>
    [Fact]
    public void The_map_is_not_the_slot_numbers()
    {
        var p = Refit(("j_hij_l", 1f));
        int start = Window(p, 12), count = Window(p, 14);

        // The host's table order, carried through the union (host bones go in first, in name order).
        Assert.Equal(TableOrder, MappedNames(p, start, count));
        Assert.NotEqual(Names.Take(count), MappedNames(p, start, count));
    }

    /// <summary>
    /// A reskin that names a bone the garment never used grows the table by one; the map has to grow with it and
    /// name the new bone, not the name-list bone that shares its slot number.
    /// </summary>
    [Fact]
    public void A_bone_the_reskin_adds_is_named_in_the_map_too()
    {
        var donor = SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh("/mt_c0201b0001_a.mtrl",
                new SyntheticModel.Sub(0, Weights: [("iv_nitoukin_l", 1f)]))]);

        var plan = new (string Bone, float W)[]?[] { [("iv_nitoukin_l", 1f)], null, null };
        var rebuilt = SecondSkinWriter.Build([], [], Garment(), out _,
                                             hostReskin: m => m == 0 ? plan : null,
                                             boneDonors: [donor]);
        var p = SecondSkinWriter.Parse(rebuilt);

        var table = Assert.Single(p.BoneTables);
        int start = Window(p, 12), count = Window(p, 14);

        Assert.Equal(TableOrder.Length + 1, table.Length);
        Assert.Contains("iv_nitoukin_l", MappedNames(p, start, count));
        Assert.Equal(table.Select(b => p.BoneNames[b]), MappedNames(p, start, count));
    }

    /// <summary>
    /// The fixture's own contract. A real mesh's table holds only the bones that mesh uses, so a SUBSET has to work
    /// — every field that measures the table has to be sized from the table, not from the model's name list. Get one
    /// wrong and the parser walks past the table's end and reads the shape block, the bone map and the bounding
    /// boxes from the wrong offsets, which is a fixture that lies rather than a test that fails.
    /// </summary>
    [Fact]
    public void A_table_holding_only_some_of_the_models_bones_still_parses()
    {
        // n_root is always interned, and no vertex follows it here — so a table without it is a genuine subset.
        // Two bones, not three: the v6 index pool is padded to an even length, and three of four names pads back
        // up to four, so a three-bone subset would pass even with the pool sized from the name list.
        string[] subset = ["j_sebo_c", "j_hij_l"];
        var mdl = SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh(Material,
                new SyntheticModel.Sub(0, Weights: [("j_hij_l", 0.6f), ("j_sebo_c", 0.4f)]))],
            SyntheticModel.V6,
            shapeNames: ["shp_base"],
            boneTableOrder: subset);

        var p = SecondSkinWriter.Parse(mdl);
        Assert.Equal(3, p.BoneNames.Length);                       // the name list keeps n_root
        Assert.Equal(subset, Assert.Single(p.BoneTables).Select(b => p.BoneNames[b]));

        // Everything the parser reaches AFTER the table — its position is what a mis-sized table field ruins.
        Assert.Equal("shp_base", Assert.Single(p.Shapes).Key);
        Assert.Equal(["atr_top"], p.AttrNames);
        Assert.Equal(p.BoneTables[0], p.SubmeshBoneMap);
        int start = Window(p, 12), count = Window(p, 14);
        Assert.Equal(subset.Length, count);
        Assert.Equal(subset, MappedNames(p, start, count));
    }

    /// <summary>
    /// And a bone a vertex follows that the table has no slot for is a broken fixture, said out loud. Silently it
    /// becomes blend index 255, which points at nothing and makes every assertion downstream meaningless.
    /// </summary>
    [Fact]
    public void A_bone_with_no_slot_in_the_table_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh(Material,
                new SyntheticModel.Sub(0, Weights: [("j_hij_l", 0.5f), ("j_sebo_c", 0.5f)]))],
            SyntheticModel.V6,
            boneTableOrder: ["j_hij_l"]));
        Assert.Contains("j_sebo_c", ex.Message);

        // The unweighted fallback follows n_root, so a table without it has to be refused for the same reason.
        var ex2 = Assert.Throws<ArgumentException>(() => SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh(Material, new SyntheticModel.Sub(0))],
            SyntheticModel.V6,
            boneTableOrder: []));
        Assert.Contains("n_root", ex2.Message);
    }

    /// <summary>
    /// Each submesh gets a window of ITS OWN, never one the whole mesh shares. Of 275 game and author models
    /// measured, every one lays the map out that way and not one shares a window — and a reader is entitled to
    /// that: xivModdingFramework, and so TexTools, rebuilds a bone set per part from this map. Lumina resolves
    /// each window wherever it points and cannot tell the difference, which is why Penumbra read a shared window
    /// quite happily while TexTools handed 3ds Max the wrong bone names.
    /// </summary>
    [Fact]
    public void Sibling_submeshes_each_get_their_own_window()
    {
        var host = SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh(Material,
                new SyntheticModel.Sub(0, Weights: [("j_hij_l", 0.4f), ("j_sebo_c", 0.3f), ("iv_fukubu_phys", 0.3f)]),
                new SyntheticModel.Sub(1, Weights: [("j_hij_l", 0.4f), ("j_sebo_c", 0.3f), ("iv_fukubu_phys", 0.3f)]))],
            SyntheticModel.V6,
            boneTableOrder: TableOrder);

        var plan = new (string Bone, float W)[]?[6];
        for (int i = 0; i < plan.Length; i++) plan[i] = [("j_hij_l", 1f)];
        var p = SecondSkinWriter.Parse(
            SecondSkinWriter.Build([], [], host, out _, hostReskin: m => m == 0 ? plan : null));

        var table = Assert.Single(p.BoneTables);
        Assert.True(p.SubmeshCount >= 2, "the fixture should have produced two submeshes");

        var starts = new List<int>();
        for (int su = 0; su < p.SubmeshCount; su++)
        {
            int start = BitConverter.ToUInt16(p.S, p.SubmeshStart + su * 16 + 12);
            int count = BitConverter.ToUInt16(p.S, p.SubmeshStart + su * 16 + 14);
            starts.Add(start);
            Assert.Equal(table.Length, count);
            Assert.Equal(table.Select(b => p.BoneNames[b]), MappedNames(p, start, count));
        }
        Assert.Equal(starts.Count, starts.Distinct().Count());
    }

    /// <summary>
    /// The whole map, for a build with CONTENT GEOMETRY as well as a host — the shape of a refit. Every submesh's
    /// window has to be its own mesh's bone table, and the map has to be exactly as long as the windows need.
    /// <para/>
    /// A source's map used to be carried over and its windows rebased. A source's windows commonly reach further
    /// than its own map, which is harmless in that file, but rebased into a merged map they land inside another
    /// source's entries: the mesh then wears another mesh's bones in every tool that reads the map, while the game,
    /// which skins from the bone table, draws it correctly. Measured on a refit, four of eight submeshes were wrong
    /// and the windows wanted 267 entries from a 159-entry map.
    /// </summary>
    [Fact]
    public void Every_window_is_its_own_meshs_table_and_the_map_is_exactly_that_long()
    {
        var host = SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh(Material,
                new SyntheticModel.Sub(0, Weights: [("j_hij_l", 0.5f), ("j_sebo_c", 0.5f)]),
                new SyntheticModel.Sub(1, Weights: [("j_hij_l", 0.5f), ("j_sebo_c", 0.5f)]))],
            SyntheticModel.V6);
        var content = SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh("/mt_c0201b0001_bibo.mtrl",
                new SyntheticModel.Sub(0, Weights: [("iv_fukubu_phys", 1f)]),
                new SyntheticModel.Sub(1, Weights: [("iv_fukubu_phys", 1f)]),
                new SyntheticModel.Sub(2, Weights: [("iv_fukubu_phys", 1f)]))],
            SyntheticModel.V6);

        var layer = new SecondSkinLayer
        {
            MaterialName = "/mt_c0201b0001_bibo.mtrl",
            Geometry = [new ContentGeometry(content, SecondSkinWriter.IsBodySkinMaterial)],
        };
        var p = SecondSkinWriter.Parse(SecondSkinWriter.Build([], [layer], host, out _));

        int need = 0;
        for (int m = 0; m < p.MeshCount; m++)
        {
            int mo = p.MeshStart + m * 36;
            int ss = BitConverter.ToUInt16(p.S, mo + 10), sc = BitConverter.ToUInt16(p.S, mo + 12);
            var table = p.BoneTables[BitConverter.ToUInt16(p.S, mo + 14)];
            Assert.True(sc > 0, $"mesh {m} kept no submeshes");
            for (int su = ss; su < ss + sc; su++)
            {
                int start = Window(p, su, 12), count = Window(p, su, 14);
                need = Math.Max(need, start + count);
                Assert.True(start + count <= p.SubmeshBoneMap.Length,
                    $"mesh {m} submesh {su}: window {start}+{count} runs past a {p.SubmeshBoneMap.Length}-entry map");
                Assert.Equal(table.Select(b => p.BoneNames[b]), MappedNames(p, start, count));
            }
        }
        // Exactly as long: no carried prefix nothing points at, and nothing missing off the end.
        Assert.Equal(need, p.SubmeshBoneMap.Length);
    }

    /// <summary>The first submesh's bone window: <c>boneStart</c> at +12, <c>boneCount</c> at +14 of its 16-byte struct.</summary>
    private static int Window(SecondSkinWriter.Source p, int field)
        => Window(p, 0, field);

    /// <summary>A given submesh's bone window field.</summary>
    private static int Window(SecondSkinWriter.Source p, int submesh, int field)
        => BitConverter.ToUInt16(p.S, p.SubmeshStart + submesh * 16 + field);
}
