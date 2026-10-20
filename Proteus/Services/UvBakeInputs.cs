using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// The body files the bibo↔TBSE transfer maps are baked from, by their path under each body mod's folder, and how each
/// is read into the bake.
/// <para/>
/// Fixed paths, not the mods' option groups: the files are what the maps describe, and a group renamed or switched off
/// (both mods' group files can be set aside by a player) must not change which body the maps are made from.
/// </summary>
internal static class UvBakeInputs
{
    private const string BiboModel = "chara/equipment/e0000/model/";

    internal const string BiboChest = "Breasts - Small Clothes/Nude - Small/" + BiboModel + "c0201e0000_top.mdl";
    internal static readonly string[] BiboChestSizes =
        ["Small", "Medium", "Large", "X-Large"];
    internal static string BiboChestOf(string size) => $"Breasts - Small Clothes/Nude - {size}/{BiboModel}c0201e0000_top.mdl";

    /// <summary>Bibo+'s legs options at size Small, the default first.</summary>
    internal static readonly (string Name, string Rel)[] BiboLegs =
    [
        ("Type A", "Bottoms - Small Clothes/Type A - Small/" + BiboModel + "c0201e0000_dwn.mdl"),
        ("Type B", "Bottoms - Small Clothes/Type B - Small/" + BiboModel + "c0201e0000_dwn.mdl"),
        ("Type C", "Bottoms - Small Clothes/Type C - Small/" + BiboModel + "c0201e0000_dwn.mdl"),
        ("SFW Undies", "Bottoms - Small Clothes/SFW Undies - Small/" + BiboModel + "c0201e0000_dwn.mdl"),
    ];

    internal const string BiboHands = "Hands/Small Clothes/" + BiboModel + "c0201e0000_glv.mdl";
    internal const string BiboFeet = "Feet/Small Clothes/" + BiboModel + "c0201e0000_sho.mdl";

    internal const string TbseChest = "common/1/c0101e0000_top.mdl";
    internal const string TbseHighlanderChest = "common/2/c0301e0000_top.mdl";
    internal const string TbseHands = "common/3/c0101e0000_glv.mdl";
    internal const string TbseFeet = "common/7/c0101e0000_sho.mdl";

    /// <summary>TBSE's smallclothes options, its default (SFW) first.</summary>
    internal static readonly (string Name, string Rel)[] TbseLegs =
    [
        ("sfw", "p2/step 6- smallclothes options/sfw/" + BiboModel + "c0101e0000_dwn.mdl"),
        ("gen a", "p2/step 6- smallclothes options/gen a/" + BiboModel + "c0101e0000_dwn.mdl"),
        ("gen b", "p2/step 6- smallclothes options/gen b/" + BiboModel + "c0101e0000_dwn.mdl"),
        ("gen c", "p2/step 6- smallclothes options/gen c/" + BiboModel + "c0101e0000_dwn.mdl"),
        ("gen d", "p2/step 6- smallclothes options/gen d/" + BiboModel + "c0101e0000_dwn.mdl"),
    ];

    /// <summary>Every file of a body mod the bake reads, relative to its folder.</summary>
    internal static IEnumerable<string> BiboFiles()
        => BiboChestSizes.Select(BiboChestOf).Concat(BiboLegs.Select(l => l.Rel)).Append(BiboHands).Append(BiboFeet);

    internal static IEnumerable<string> TbseFiles()
        => new[] { TbseChest, TbseHighlanderChest, TbseHands, TbseFeet }.Concat(TbseLegs.Select(l => l.Rel));

    /// <summary>The files under <paramref name="root"/>, read; throws naming the first one missing.</summary>
    internal static Dictionary<string, byte[]> Read(string root, IEnumerable<string> rels)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var rel in rels)
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new FileNotFoundException($"the bake needs {rel} under {root}", path);
            files[rel] = File.ReadAllBytes(path);
        }
        return files;
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>One model's LOD0 skin as a bake part, or null when it has none or cannot be read.</summary>
    internal static BakeBody.Part? Part(byte[] mdl, int option = 0)
    {
        if (!SecondSkinWriter.TryReadLod0Geometry(mdl, out var pos, out var uv, out var tri, out var w, out var n,
                                                  skinOnly: true) || tri.Length == 0)
            return null;
        return new BakeBody.Part(pos, uv, tri, w, n, option);
    }

    /// <summary>
    /// A TBSE model bent from a man's proportions to a woman's the way the game bends men's gear on a woman: the game's
    /// racial deformer, c0101 to c0201, with a body mod's own bones bent as the standard bones they stand in for.
    /// </summary>
    internal static byte[] Bend(byte[] mdl, PbdFile pbd, Func<string, string?> parentOf)
        => RacialModelBake.Bake(mdl, 101, 201, pbd, parentOf, out var refusal, UvBakeBones.Alias)
           ?? throw new InvalidDataException("the TBSE model could not be bent to c0201: " + refusal);
}
