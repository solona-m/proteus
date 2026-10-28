using System;
using System.IO;
using System.Linq;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The skeleton reader on real files: the game's own (tagfile version 3) and the custom skeletons mods ship (version 5,
/// written by Havok 2014 tools), every one in the mod folder — and none of them, cut short anywhere, throws.
/// </summary>
[Trait("Speed", "Slow")]
public class HavokSkeletonLocalTests(ITestOutputHelper output)
{
    private const string Ivcs = LocalData.Mods + ":[IVCS] Illusio Vitae Custom Skeleton Base V1.1 (2)";
    private const string IvcsHighlander = Ivcs + "/chara/human/c0301/skeleton/base/b0001/skl_c0301b0001.sklb";

    internal static string GameSkeleton(int race) => $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb";

    internal static byte[] GameFile(string path)
        => new Lumina.GameData(LocalData.Path(LocalData.GameData)).GetFile(path)!.Data;

    /// <summary>The spine every human skeleton has, root-most last.</summary>
    private static readonly string[] Spine = ["j_kao", "j_kubi", "j_sebo_c", "j_sebo_b", "j_sebo_a", "n_hara", "n_root"];

    private static void AssertHumanTree(HavokSkeleton? skeleton)
    {
        Assert.NotNull(skeleton);
        Assert.Equal(1, skeleton!.Parents.Count(p => p < 0));
        for (int i = 0; i < skeleton.Parents.Count; i++) Assert.True(skeleton.Parents[i] < i);

        var parents = skeleton.ParentMap();
        for (int i = 0; i < Spine.Length - 1; i++) Assert.Equal(Spine[i + 1], parents[Spine[i]]);
        Assert.Equal("n_hara", parents["j_kosi"]);
        Assert.Null(parents["n_root"]);
    }

    [LocalDataTheory(LocalData.GameData)]
    [InlineData(101)]
    [InlineData(201)]
    [InlineData(301)]
    [InlineData(1501)]
    [Trait("Plan", "AC-refit-other-race-1")]
    public void The_game_s_race_skeletons_read_as_a_human_tree(int race)
    {
        var skeleton = HavokSkeleton.Read(GameFile(GameSkeleton(race)));
        AssertHumanTree(skeleton);
        output.WriteLine($"c{race:D4}: {skeleton!.Names.Count} bones");
    }

    /// <summary>
    /// The IVCS file holds three skeletons — its own and the two a skeleton mapper maps between — and the one its
    /// animation container names is the custom one, with the IVCS bones the game's lacks.
    /// </summary>
    [LocalDataFact(LocalData.GameData, IvcsHighlander)]
    [Trait("Plan", "AC-refit-other-race-1")]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void A_custom_skeleton_is_read_from_its_animation_container_with_its_own_bones()
    {
        var ivcs = HavokSkeleton.Read(File.ReadAllBytes(LocalData.Path(IvcsHighlander)));
        var game = HavokSkeleton.Read(GameFile(GameSkeleton(301)));
        AssertHumanTree(ivcs);
        AssertHumanTree(game);

        var extra = ivcs!.Names.Except(game!.Names).ToList();
        output.WriteLine($"IVCS c0301: {ivcs.Names.Count} bones, {extra.Count} not in the game's: {string.Join(", ", extra.Take(20))}");
        Assert.NotEmpty(extra);
        // Every bone the game's skeleton has keeps its parent.
        var gameParents = game.ParentMap();
        var ivcsParents = ivcs.ParentMap();
        foreach (var (bone, parent) in gameParents)
            if (ivcsParents.TryGetValue(bone, out var p)) Assert.Equal(parent, p);
    }

    /// <summary>Every skeleton in the mod folder reads, or is turned down — never an exception.</summary>
    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void Every_skeleton_mods_ship_reads_without_throwing()
    {
        var files = Directory.EnumerateFiles(LocalData.Path(LocalData.Mods), "*.sklb", SearchOption.AllDirectories)
                             .Take(600).ToList();
        int read = 0, human = 0;
        var refused = new System.Collections.Generic.List<string>();
        foreach (var f in files)
        {
            var s = HavokSkeleton.Read(File.ReadAllBytes(f), out string why);
            if (s == null) { refused.Add(f + " — " + why); continue; }
            read++;
            if (Path.GetFileName(f).StartsWith("skl_c", StringComparison.OrdinalIgnoreCase)
                && f.Replace('\\', '/').Contains("/skeleton/base/", StringComparison.OrdinalIgnoreCase))
            {
                AssertHumanTree(s);
                human++;
            }
        }
        output.WriteLine($"{files.Count} files, {read} read, {human} human base skeletons; refused: {refused.Count}");
        foreach (var f in refused.Take(20)) output.WriteLine("  refused " + f);
        Assert.True(human > 0);
        Assert.Empty(refused);
    }

    /// <summary>Cut short at any point, or with a byte flipped, a file reads as null — the game process never dies of it.</summary>
    [LocalDataFact(LocalData.GameData, IvcsHighlander)]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void A_damaged_skeleton_reads_as_nothing_rather_than_throwing()
    {
        foreach (var bytes in new[] { File.ReadAllBytes(LocalData.Path(IvcsHighlander)), GameFile(GameSkeleton(101)) })
        {
            for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 400))
                _ = HavokSkeleton.Read(bytes.AsSpan(0, cut));

            var random = new Random(1234);
            for (int round = 0; round < 300; round++)
            {
                var damaged = (byte[])bytes.Clone();
                for (int k = 0; k < 4; k++) damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
                _ = HavokSkeleton.Read(damaged);
            }
        }
    }
}
