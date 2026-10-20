using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// The body regions a transfer-map bake matches within, and the bones body mods rig their skin to that the game's
/// skeleton does not have.
/// <para/>
/// A texel of one body may only land on the other body's skin in the same part of the body or the part next to it: a
/// point on the inner thigh must not be carried to the other thigh, nor a fingertip to the hip it hangs beside. The
/// part is read from the bone a vertex is weighted to most.
/// <para/>
/// Body mods rig skin to bones of their own. TBSE's legs are weighted to <c>n_ear_b_l</c>/<c>_r</c> — earring nodes —
/// Bibo+'s legs to <c>n_root</c>, its feet to a heel offset, TBSE-X to IVCS physics bones. The racial deformer finds a
/// bone by its own name before it walks up the skeleton, so an earring node bends the crotch as an ear would; and no
/// region is known for any of them. <see cref="Alias"/> names the bone each is bent as, and the region table treats
/// them as voting for a region or not at all.
/// </summary>
internal static class UvBakeBones
{
    internal enum Region : byte
    {
        None, Head, Torso, Pelvis, Genital,
        UpperArmL, UpperArmR, ForearmL, ForearmR, HandL, HandR,
        ThighL, ThighR, CalfL, CalfR, FootL, FootR,
    }

    /// <summary>How a weighted bone was resolved, for the bake's report.</summary>
    internal enum Resolution { Own, Override, Absent }

    /// <summary>The bone <paramref name="bone"/> is bent as: itself, unless it is a body mod's own.</summary>
    internal static string Alias(string bone) => Override(bone) ?? bone;

    /// <summary>The standard bone a body mod's own bone stands in for, or null when it is not one.</summary>
    internal static string? Override(string bone)
    {
        if (bone.StartsWith("n_ear_", StringComparison.Ordinal)) return "j_kosi";
        if (bone is "n_root" || bone.StartsWith("heels_offset", StringComparison.Ordinal)) return "j_kosi";
        if (bone.StartsWith("iv_daitai_phys_l", StringComparison.Ordinal)) return "j_asi_a_l";
        if (bone.StartsWith("iv_daitai_phys_r", StringComparison.Ordinal)) return "j_asi_a_r";
        if (bone.StartsWith("iv_", StringComparison.Ordinal)) return "j_kosi";
        return null;
    }

    /// <summary>
    /// Which region a vertex weighted to <paramref name="bone"/> votes for; <see cref="Region.None"/> when the bone
    /// casts no vote (a body mod's root or offset node), so the vertex takes its neighbours' region instead.
    /// </summary>
    internal static Region RegionOf(string bone)
    {
        if (bone.StartsWith("iv_kintama", StringComparison.Ordinal) || bone.StartsWith("iv_funyachin", StringComparison.Ordinal)
            || bone.StartsWith("iv_koumon", StringComparison.Ordinal)) return Region.Genital;
        if (bone.StartsWith("iv_shiri", StringComparison.Ordinal)) return Region.Pelvis;
        if (bone.StartsWith("iv_daitai_phys_l", StringComparison.Ordinal)) return Region.ThighL;
        if (bone.StartsWith("iv_daitai_phys_r", StringComparison.Ordinal)) return Region.ThighR;
        if (Override(bone) != null) return Region.None;

        bool l = bone.EndsWith("_l", StringComparison.Ordinal), r = bone.EndsWith("_r", StringComparison.Ordinal);
        Region Side(Region left, Region right) => l ? left : r ? right : Region.None;

        if (bone.StartsWith("j_kao", StringComparison.Ordinal) || bone.StartsWith("j_kubi", StringComparison.Ordinal)
            || bone.StartsWith("j_f_", StringComparison.Ordinal) || bone.StartsWith("j_mimi", StringComparison.Ordinal)
            || bone.StartsWith("j_kami", StringComparison.Ordinal) || bone.StartsWith("n_kubi", StringComparison.Ordinal))
            return Region.Head;
        if (bone.StartsWith("j_sebo", StringComparison.Ordinal) || bone.StartsWith("j_mune", StringComparison.Ordinal)
            || bone.StartsWith("j_sako", StringComparison.Ordinal)) return Region.Torso;
        if (bone.StartsWith("j_kosi", StringComparison.Ordinal) || bone.StartsWith("j_shiri", StringComparison.Ordinal))
            return Region.Pelvis;
        if (bone.StartsWith("j_ude_a", StringComparison.Ordinal) || bone.StartsWith("n_hkata", StringComparison.Ordinal)
            || bone.StartsWith("n_hhiji", StringComparison.Ordinal))
            return Side(Region.UpperArmL, Region.UpperArmR);
        if (bone.StartsWith("j_ude_b", StringComparison.Ordinal) || bone.StartsWith("n_hte", StringComparison.Ordinal)
            || bone.StartsWith("n_hijisoubi", StringComparison.Ordinal)) return Side(Region.ForearmL, Region.ForearmR);
        if (bone.StartsWith("j_te", StringComparison.Ordinal) || bone.StartsWith("j_oya", StringComparison.Ordinal)
            || bone.StartsWith("j_hito", StringComparison.Ordinal) || bone.StartsWith("j_naka", StringComparison.Ordinal)
            || bone.StartsWith("j_kusu", StringComparison.Ordinal) || bone.StartsWith("j_ko_", StringComparison.Ordinal))
            return Side(Region.HandL, Region.HandR);
        if (bone.StartsWith("j_asi_a", StringComparison.Ordinal) || bone.StartsWith("n_hizasoubi", StringComparison.Ordinal))
            return Side(Region.ThighL, Region.ThighR);
        if (bone.StartsWith("j_asi_b", StringComparison.Ordinal) || bone.StartsWith("j_asi_c", StringComparison.Ordinal))
            return Side(Region.CalfL, Region.CalfR);
        if (bone.StartsWith("j_asi_d", StringComparison.Ordinal) || bone.StartsWith("j_asi_e", StringComparison.Ordinal)
            || bone.StartsWith("j_asi_f", StringComparison.Ordinal) || bone.StartsWith("j_asi_g", StringComparison.Ordinal))
            return Side(Region.FootL, Region.FootR);
        return Region.None;
    }

    private static readonly HashSet<(Region, Region)> Links =
    [
        (Region.Head, Region.Torso), (Region.Torso, Region.Pelvis), (Region.Torso, Region.UpperArmL),
        (Region.Torso, Region.UpperArmR), (Region.UpperArmL, Region.ForearmL), (Region.UpperArmR, Region.ForearmR),
        (Region.ForearmL, Region.HandL), (Region.ForearmR, Region.HandR), (Region.Pelvis, Region.ThighL),
        (Region.Pelvis, Region.ThighR), (Region.Pelvis, Region.Genital), (Region.ThighL, Region.Genital),
        (Region.ThighR, Region.Genital), (Region.ThighL, Region.CalfL), (Region.ThighR, Region.CalfR),
        (Region.CalfL, Region.FootL), (Region.CalfR, Region.FootR),
    ];

    /// <summary>Whether skin of region <paramref name="a"/> may land on skin of region <paramref name="b"/>.</summary>
    internal static bool Adjacent(Region a, Region b)
        => a == b || a == Region.None || b == Region.None || Links.Contains((a, b)) || Links.Contains((b, a));

    /// <summary>
    /// Each vertex's region: the region its heaviest voting bone votes for, and for a vertex with no voting bone, the
    /// region of the nearest voting vertex by mesh adjacency (breadth first over the triangles). A vertex joined to no
    /// voting vertex at all keeps <see cref="Region.None"/>, which is adjacent to everything.
    /// </summary>
    internal static Region[] VertexRegions((string Bone, float W)[][] weights, int[] triangles)
    {
        int n = weights.Length;
        var region = new Region[n];
        for (int v = 0; v < n; v++)
        {
            float best = 0f;
            foreach (var (bone, w) in weights[v])
            {
                var r = RegionOf(bone);
                if (r == Region.None || w <= best) continue;
                best = w;
                region[v] = r;
            }
        }

        var neighbours = new List<int>[n];
        for (int t = 0; t + 2 < triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                (neighbours[a] ??= []).Add(b);
                (neighbours[b] ??= []).Add(a);
            }

        var queue = new Queue<int>();
        for (int v = 0; v < n; v++) if (region[v] != Region.None) queue.Enqueue(v);
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            if (neighbours[v] == null) continue;
            foreach (int u in neighbours[v])
                if (region[u] == Region.None) { region[u] = region[v]; queue.Enqueue(u); }
        }
        return region;
    }

    /// <summary>
    /// How each weighted bone of a body resolves for the bend and how many vertices it carries most of — the bake's
    /// report, so a bone no table knows is seen rather than silently bent as identity.
    /// </summary>
    internal static List<(string Bone, int Vertices, Resolution How)> Report((string Bone, float W)[][] weights,
                                                                             Func<string, bool> inSkeleton)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var vw in weights)
        {
            string? top = null;
            float best = 0f;
            foreach (var (bone, w) in vw)
                if (w > best) { best = w; top = bone; }
            if (top != null) counts[top] = counts.GetValueOrDefault(top) + 1;
        }
        var result = new List<(string, int, Resolution)>();
        foreach (var (bone, count) in counts)
            result.Add((bone, count, Override(bone) != null ? Resolution.Override
                                    : inSkeleton(bone) ? Resolution.Own : Resolution.Absent));
        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }
}
